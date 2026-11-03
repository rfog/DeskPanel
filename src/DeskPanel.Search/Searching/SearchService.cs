using DeskPanel.Search.Indexing;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Search.Highlight;

namespace DeskPanel.Search.Searching;

public sealed class SearchService
{
    public Task<SearchResult> SearchAsync(IReadOnlyList<SearchTarget> targets, string queryText, int maxHits, CancellationToken ct = default) =>
        Task.Run(() => Search(targets, queryText, maxHits), ct);

    private static SearchResult Search(IReadOnlyList<SearchTarget> targets, string queryText, int maxHits)
    {
        if (string.IsNullOrWhiteSpace(queryText) || targets.Count == 0)
        {
            return new SearchResult(Array.Empty<SearchHit>(), 0, Array.Empty<string>());
        }

        var opened = new List<(SearchTarget Target, DirectoryReader Reader, Lucene.Net.Store.Directory Directory)>();
        try
        {
            foreach (var target in targets)
            {
                var directory = Lucene.Net.Store.FSDirectory.Open(target.IndexDirectory);
                if (!DirectoryReader.IndexExists(directory))
                {
                    directory.Dispose();
                    continue;
                }

                opened.Add((target, DirectoryReader.Open(directory), directory));
            }

            if (opened.Count == 0)
            {
                return new SearchResult(Array.Empty<SearchHit>(), 0, Array.Empty<string>());
            }

            // A single MultiReader keeps term statistics global, so scores are comparable across indexes.
            using var multiReader = new MultiReader(opened.Select(o => (IndexReader)o.Reader).ToArray(), false);
            var searcher = new IndexSearcher(multiReader);
            using var analyzer = IndexSchema.CreateAnalyzer();

            var query = Parse(queryText, analyzer);
            var topDocs = searcher.Search(query, maxHits);

            var terms = ExtractTerms(query, multiReader);
            var scorer = new QueryScorer(query, IndexSchema.Content);
            var highlighter = new Highlighter(
                new SimpleHTMLFormatter(SearchHit.HighlightStart.ToString(), SearchHit.HighlightEnd.ToString()), scorer)
            {
                TextFragmenter = new SimpleSpanFragmenter(scorer, 180),

                // The default (50 KB) misses matches deep inside long PDFs, producing hits with empty snippets.
                MaxDocCharsToAnalyze = 2_000_000,
            };

            // Doc ids in a MultiReader are the sub-readers' ids offset by the preceding sub-readers' MaxDoc.
            var bases = new int[opened.Count];
            for (int i = 1; i < opened.Count; i++)
            {
                bases[i] = bases[i - 1] + opened[i - 1].Reader.MaxDoc;
            }

            var hits = new List<SearchHit>(topDocs.ScoreDocs.Length);
            foreach (var scoreDoc in topDocs.ScoreDocs)
            {
                var owner = Array.FindLastIndex(bases, b => b <= scoreDoc.Doc);
                var target = opened[owner].Target;
                var doc = searcher.Doc(scoreDoc.Doc);
                var relative = doc.Get(IndexSchema.Path) ?? "";
                var content = doc.Get(IndexSchema.Content) ?? "";

                hits.Add(new SearchHit(
                    target.IndexName,
                    relative,
                    Path.GetFullPath(Path.Combine(target.ResolvedBaseRoot, relative.Replace('/', Path.DirectorySeparatorChar))),
                    doc.Get(IndexSchema.Extension) ?? Path.GetExtension(relative),
                    scoreDoc.Score,
                    BuildSnippet(highlighter, analyzer, content)));
            }

            return new SearchResult(hits, topDocs.TotalHits, terms);
        }
        finally
        {
            foreach (var (_, reader, directory) in opened)
            {
                reader.Dispose();
                directory.Dispose();
            }
        }
    }

    private static Query Parse(string queryText, Lucene.Net.Analysis.Analyzer analyzer)
    {
        var parser = new MultiFieldQueryParser(IndexSchema.MatchVersion, new[] { IndexSchema.Content, IndexSchema.Name }, analyzer)
        {
            // AND matches desktop-search expectations: adding a word narrows the results instead of widening them.
            DefaultOperator = QueryParserBase.AND_OPERATOR,
            AllowLeadingWildcard = true,
        };

        try
        {
            return parser.Parse(queryText);
        }
        catch (ParseException)
        {
            // Unbalanced quotes or stray operators are common when typing; fall back to a literal search.
            return parser.Parse(QueryParserBase.Escape(queryText));
        }
    }

    private static IReadOnlyList<string> ExtractTerms(Query query, IndexReader reader)
    {
        var terms = new HashSet<Term>();
        try
        {
            // Wildcards and prefixes only expose concrete terms after rewriting against the reader.
            query.Rewrite(reader).ExtractTerms(terms);
        }
        catch (NotSupportedException)
        {
            return Array.Empty<string>();
        }

        return terms
            .Where(t => t.Field == IndexSchema.Content)
            .Select(t => t.Text)
            .Distinct(StringComparer.Ordinal)
            .Take(50)
            .ToList();
    }

    private static string BuildSnippet(Highlighter highlighter, Lucene.Net.Analysis.Analyzer analyzer, string content)
    {
        if (content.Length == 0)
        {
            return "";
        }

        try
        {
            using var stream = analyzer.GetTokenStream(IndexSchema.Content, new StringReader(content));
            var fragment = highlighter.GetBestFragments(stream, content, 3, " … ");
            if (!string.IsNullOrWhiteSpace(fragment))
            {
                return Collapse(fragment);
            }
        }
        catch (InvalidTokenOffsetsException)
        {
            // Falls through to a plain prefix; a missing highlight is better than dropping the hit.
        }

        return Collapse(content.Length > 300 ? content[..300] + "…" : content);
    }

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
