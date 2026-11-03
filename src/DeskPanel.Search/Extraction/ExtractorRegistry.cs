namespace DeskPanel.Search.Extraction;

public sealed class ExtractorRegistry
{
    private readonly Dictionary<string, ITextExtractor> _byExtension = new(StringComparer.OrdinalIgnoreCase);

    public ExtractorRegistry(IEnumerable<ITextExtractor> extractors)
    {
        foreach (var extractor in extractors)
        {
            foreach (var extension in extractor.Extensions)
            {
                _byExtension[extension] = extractor;
            }
        }
    }

    public static ExtractorRegistry CreateDefault()
    {
        // The zip extractor needs the registry itself to dispatch its entries, hence the deferred lookup.
        ExtractorRegistry? registry = null;
        registry = new ExtractorRegistry(new ITextExtractor[]
        {
            new PlainTextExtractor(),
            new PdfTextExtractor(),
            new HtmlTextExtractor(),
            new OpenXmlTextExtractor(),
            new OpenDocumentTextExtractor(),
            new EpubTextExtractor(),
            new RtfTextExtractor(),
            new ZipTextExtractor(() => registry!),
        });
        return registry;
    }

    public IReadOnlyList<string> SupportedExtensions => _byExtension.Keys.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();

    public bool TryGet(string extension, out ITextExtractor extractor) => _byExtension.TryGetValue(extension, out extractor!);

    public bool IsSupported(string extension) => _byExtension.ContainsKey(extension);
}
