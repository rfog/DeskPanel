using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.Miscellaneous;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Util;

namespace DeskPanel.Search.Indexing;

public static class IndexSchema
{
    /// <summary>
    /// Bump whenever fields or the analyzer change. Indexes are copied between machines, so a silent mismatch
    /// would produce wrong results instead of an error; the version check forces a rebuild instead.
    /// </summary>
    public const int Version = 1;

    public const LuceneVersion MatchVersion = LuceneVersion.LUCENE_48;

    public const string PathKey = "pathkey";
    public const string Path = "path";
    public const string Name = "name";
    public const string Extension = "ext";
    public const string ModifiedTicks = "mtime";
    public const string Size = "size";
    public const string Hash = "sha256";
    public const string Content = "content";

    public const string MetaFileName = "deskpanel-index.json";

    public static Analyzer CreateAnalyzer() => new FoldingAnalyzer();

    /// <summary>Windows paths are case-insensitive; the key must be too, or a rename-by-case duplicates documents.</summary>
    public static string MakeKey(string relativePath) => relativePath.Replace('\\', '/').ToUpperInvariant();

    /// <summary>
    /// Standard tokenization plus lower-casing and accent folding, so "canción" and "cancion" match.
    /// No stemming: the corpus mixes Spanish and English and a single-language stemmer hurts the other one.
    /// </summary>
    private sealed class FoldingAnalyzer : Analyzer
    {
        protected override TokenStreamComponents CreateComponents(string fieldName, TextReader reader)
        {
            var tokenizer = new StandardTokenizer(MatchVersion, reader);
            TokenStream stream = new StandardFilter(MatchVersion, tokenizer);
            stream = new LowerCaseFilter(MatchVersion, stream);
            stream = new ASCIIFoldingFilter(stream);
            return new TokenStreamComponents(tokenizer, stream);
        }
    }
}
