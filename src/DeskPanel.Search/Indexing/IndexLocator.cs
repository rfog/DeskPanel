using DeskPanel.Core.Configuration;
using DeskPanel.Core.IO;

namespace DeskPanel.Search.Indexing;

public static class IndexLocator
{
    public static string GetIndexDirectory(string resolvedStorageFolder, IndexDefinition definition) =>
        Path.Combine(resolvedStorageFolder, FileNames.Sanitize(definition.Name, 80));
}
