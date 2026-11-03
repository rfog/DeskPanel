using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using DeskPanel.Core.Configuration;
using DeskPanel.Search.Extraction;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using LuceneDirectory = Lucene.Net.Store.Directory;

namespace DeskPanel.Search.Indexing;

/// <summary>
/// Incremental indexer. Documents are keyed by path relative to the index base root, never by absolute path,
/// so an index built on one machine can be copied to another and only updated there.
/// </summary>
public sealed class FolderIndexer
{
    // Cloud placeholder bits (not exposed by System.IO.FileAttributes): opening such a file triggers a download.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private static readonly HashSet<string> StoredFieldsForDiff = new(StringComparer.Ordinal)
    {
        IndexSchema.Path, IndexSchema.ModifiedTicks, IndexSchema.Size, IndexSchema.Hash,
    };

    private readonly ExtractorRegistry _extractors;

    public FolderIndexer(ExtractorRegistry extractors) => _extractors = extractors;

    public IReadOnlyList<string> SupportedExtensions => _extractors.SupportedExtensions;

    public Task<IndexUpdateResult> UpdateAsync(
        IndexDefinition definition,
        string resolvedBaseRoot,
        string indexDirectory,
        bool rebuild,
        IProgress<IndexProgress>? progress,
        CancellationToken ct) =>
        Task.Run(() => Update(definition, resolvedBaseRoot, indexDirectory, rebuild, progress, ct), ct);

    private IndexUpdateResult Update(
        IndexDefinition definition,
        string baseRoot,
        string indexDirectory,
        bool rebuild,
        IProgress<IndexProgress>? progress,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new IndexUpdateResult();

        if (!System.IO.Directory.Exists(baseRoot))
        {
            throw new DirectoryNotFoundException($"Base folder not found: {baseRoot}");
        }

        var metadata = IndexMetadata.TryLoad(indexDirectory);
        if (metadata is not null && metadata.SchemaVersion != IndexSchema.Version)
        {
            // An index written with a different analyzer or field layout cannot be patched incrementally.
            rebuild = true;
        }

        if (rebuild && System.IO.Directory.Exists(indexDirectory))
        {
            System.IO.Directory.Delete(indexDirectory, recursive: true);
        }

        System.IO.Directory.CreateDirectory(indexDirectory);

        progress?.Report(new IndexProgress("Enumerando ficheros", 0, 0, null));
        var files = EnumerateCandidates(definition, baseRoot, ct);

        using LuceneDirectory directory = Lucene.Net.Store.FSDirectory.Open(indexDirectory);
        var existing = LoadExistingState(directory);

        using var analyzer = IndexSchema.CreateAnalyzer();
        var config = new IndexWriterConfig(IndexSchema.MatchVersion, analyzer) { OpenMode = OpenMode.CREATE_OR_APPEND };
        using var writer = new IndexWriter(directory, config);

        var seenKeys = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var processed = 0;

        var options = new ParallelOptions
        {
            CancellationToken = ct,

            // PDF extraction is CPU-bound, but leaving cores free keeps the desktop responsive during long rebuilds.
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
        };

        Parallel.ForEach(files, options, file =>
        {
            var relative = Path.GetRelativePath(baseRoot, file.FullName).Replace('\\', '/');
            var key = IndexSchema.MakeKey(relative);
            seenKeys[key] = 0;

            try
            {
                ProcessFile(writer, file, relative, key, existing, result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (result.Failures)
                {
                    result.Failures.Add($"{relative}: {ex.Message}");
                }
            }

            var done = Interlocked.Increment(ref processed);
            if (done % 25 == 0 || done == files.Count)
            {
                progress?.Report(new IndexProgress("Indexando", done, files.Count, relative));
            }
        });

        foreach (var key in existing.Keys.Where(k => !seenKeys.ContainsKey(k)))
        {
            writer.DeleteDocuments(new Term(IndexSchema.PathKey, key));
            result.Removed++;
        }

        progress?.Report(new IndexProgress("Guardando índice", files.Count, files.Count, null));
        writer.Commit();

        new IndexMetadata
        {
            SchemaVersion = IndexSchema.Version,
            Name = definition.Name,
            BaseRoot = definition.BaseRoot,
            LastUpdatedUtc = DateTimeOffset.UtcNow,
            DocumentCount = Math.Max(0, existing.Count + result.Added - result.Removed),
            LastUpdatedOnMachine = Environment.MachineName,
        }.Save(indexDirectory);

        result.Elapsed = stopwatch.Elapsed;
        return result;
    }

    private void ProcessFile(
        IndexWriter writer,
        FileInfo file,
        string relative,
        string key,
        IReadOnlyDictionary<string, ExistingDoc> existing,
        IndexUpdateResult result)
    {
        var ticks = file.LastWriteTimeUtc.Ticks;
        var size = file.Length;

        if (existing.TryGetValue(key, out var previous))
        {
            if (previous.Size == size && previous.ModifiedTicks == ticks)
            {
                Interlocked.Increment(ref result.Unchanged);
                return;
            }

            // Sync clients and copies between machines often rewrite timestamps without changing content.
            // Hashing is far cheaper than re-extracting a PDF, so check content identity before doing real work.
            if (previous.Size == size && previous.Hash is not null)
            {
                var hash = ComputeHash(file.FullName);
                if (string.Equals(hash, previous.Hash, StringComparison.Ordinal) && previous.Content is not null)
                {
                    writer.UpdateDocument(new Term(IndexSchema.PathKey, key),
                        BuildDocument(relative, key, file.Extension, ticks, size, hash, previous.Content));
                    Interlocked.Increment(ref result.MetadataOnly);
                    return;
                }
            }
        }

        var contentHash = ComputeHash(file.FullName);
        var text = _extractors.TryGet(file.Extension, out var extractor) ? extractor.Extract(file.FullName) : "";
        writer.UpdateDocument(new Term(IndexSchema.PathKey, key),
            BuildDocument(relative, key, file.Extension, ticks, size, contentHash, text));

        if (previous is null)
        {
            Interlocked.Increment(ref result.Added);
        }
        else
        {
            Interlocked.Increment(ref result.Updated);
        }
    }

    private List<FileInfo> EnumerateCandidates(IndexDefinition definition, string baseRoot, CancellationToken ct)
    {
        // An empty list means "every supported type", so new extractors apply to hand-written definitions too.
        var extensions = definition.Extensions.Count == 0
            ? null
            : new HashSet<string>(
                definition.Extensions.Select(e => e.StartsWith('.') ? e : "." + e),
                StringComparer.OrdinalIgnoreCase);

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = definition.Recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            MatchType = MatchType.Win32,
        };

        var unique = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in NormalizeIncludedFolders(definition.IncludedFolders))
        {
            var full = folder.Length == 0 ? baseRoot : Path.Combine(baseRoot, folder.Replace('/', Path.DirectorySeparatorChar));
            if (!System.IO.Directory.Exists(full))
            {
                continue;
            }

            foreach (var path in System.IO.Directory.EnumerateFiles(full, "*", enumeration))
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(path);
                if (extensions is not null && !extensions.Contains(info.Extension) || !_extractors.IsSupported(info.Extension))
                {
                    continue;
                }

                if (definition.SkipCloudPlaceholders && IsCloudPlaceholder(info))
                {
                    continue;
                }

                unique.TryAdd(info.FullName, info);
            }
        }

        return unique.Values.ToList();
    }

    /// <summary>Drops folders already covered by a selected ancestor, so overlapping selections are not walked twice.</summary>
    public static List<string> NormalizeIncludedFolders(IEnumerable<string> folders)
    {
        var cleaned = folders
            .Select(f => f.Replace('\\', '/').Trim('/').Trim())
            .Where(f => f != ".")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f.Length)
            .ToList();

        if (cleaned.Count == 0 || cleaned.Contains(""))
        {
            return new List<string> { "" };
        }

        var result = new List<string>();
        foreach (var folder in cleaned)
        {
            if (!result.Any(parent => folder.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(folder);
            }
        }

        return result;
    }

    private static bool IsCloudPlaceholder(FileInfo info)
    {
        var attributes = info.Attributes;
        return (attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;
    }

    private static Dictionary<string, ExistingDoc> LoadExistingState(LuceneDirectory directory)
    {
        var state = new Dictionary<string, ExistingDoc>(StringComparer.Ordinal);
        if (!DirectoryReader.IndexExists(directory))
        {
            return state;
        }

        using var reader = DirectoryReader.Open(directory);
        var liveDocs = MultiFields.GetLiveDocs(reader);
        for (var docId = 0; docId < reader.MaxDoc; docId++)
        {
            if (liveDocs is not null && !liveDocs.Get(docId))
            {
                continue;
            }

            // Loading only the diff fields avoids pulling every stored content blob into memory.
            var doc = reader.Document(docId, StoredFieldsForDiff);
            var path = doc.Get(IndexSchema.Path);
            if (path is null)
            {
                continue;
            }

            state[IndexSchema.MakeKey(path)] = new ExistingDoc(
                long.TryParse(doc.Get(IndexSchema.ModifiedTicks), out var ticks) ? ticks : 0,
                long.TryParse(doc.Get(IndexSchema.Size), out var size) ? size : -1,
                doc.Get(IndexSchema.Hash),
                new Lazy<string?>(() => LoadContent(directory, path)));
        }

        return state;
    }

    // Content is only needed for the rare metadata-only rewrite, so it is fetched lazily with its own reader.
    private static string? LoadContent(LuceneDirectory directory, string path)
    {
        using var reader = DirectoryReader.Open(directory);
        var searcher = new Lucene.Net.Search.IndexSearcher(reader);
        var hits = searcher.Search(new Lucene.Net.Search.TermQuery(new Term(IndexSchema.PathKey, IndexSchema.MakeKey(path))), 1);
        return hits.TotalHits == 0 ? null : searcher.Doc(hits.ScoreDocs[0].Doc).Get(IndexSchema.Content);
    }

    private static Document BuildDocument(string relative, string key, string extension, long ticks, long size, string hash, string content)
    {
        return new Document
        {
            new StringField(IndexSchema.PathKey, key, Field.Store.NO),
            new StoredField(IndexSchema.Path, relative),
            new TextField(IndexSchema.Name, Path.GetFileName(relative), Field.Store.YES),
            new StringField(IndexSchema.Extension, extension.ToLowerInvariant(), Field.Store.YES),
            new StoredField(IndexSchema.ModifiedTicks, ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new StoredField(IndexSchema.Size, size.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new StoredField(IndexSchema.Hash, hash),

            // Stored so the highlighter can build snippets without re-extracting PDFs at query time.
            new TextField(IndexSchema.Content, content, Field.Store.YES),
        };
    }

    private static string ComputeHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record ExistingDoc(long ModifiedTicks, long Size, string? Hash, Lazy<string?> LazyContent)
    {
        public string? Content => LazyContent.Value;
    }
}
