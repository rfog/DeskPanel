using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks; // Necessary for Task context if needed, though mostly logic here.
using DeskPanel.Core.Configuration;
using DeskPanel.Core.IO;

namespace VerificaFicheros
{
    class FolderController
    {
        public List<string> MonitoredFolders = new List<string>();
        public List<Record> FilesNotMatching = new List<Record>();

        private const string m_databaseFileName = "zxFileList.txt";
        private string m_fullPath, m_monitoredFolderList;

        private string m_selectedFolder, m_selectedDatabaseFileName;

        // Added internal list to manage state safely
        private readonly List<Record> m_records = new List<Record>();

        /// <summary>
        /// The list lives in the DeskPanel config directory so it follows the configuration (and gets copied with it
        /// when that directory changes). <paramref name="legacyDirectory"/> is where earlier versions kept it, next
        /// to the exe: its list is carried over once, if the config directory has none yet.
        /// </summary>
        public void Load(string configDirectory, PathResolver paths, string legacyDirectory = null)
        {
            m_fullPath = configDirectory;
            m_paths = paths;
            if (!Directory.Exists(m_fullPath))
                Directory.CreateDirectory(m_fullPath);

            m_monitoredFolderList = Path.Combine(m_fullPath, ConfigService.MonitoredFoldersFileName);

            if (!File.Exists(m_monitoredFolderList) && legacyDirectory != null)
            {
                var legacy = Path.Combine(legacyDirectory, ConfigService.MonitoredFoldersFileName);
                if (File.Exists(legacy))
                    AtomicFile.ReplaceWith(legacy, m_monitoredFolderList);
            }

            if (File.Exists(m_monitoredFolderList))
            {
                MonitoredFolders = File.ReadAllLines(m_monitoredFolderList)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .Select(ToLocalPath)
                    .ToList();
            }
        }

        // The file is shared between machines, so it stores {Alias}/... paths (see PathResolver); in memory the
        // list holds this machine's real paths. A line whose alias is undefined here is kept verbatim.
        private PathResolver m_paths;

        private string ToLocalPath(string stored) =>
            m_paths != null && m_paths.TryResolve(stored, out var resolved) ? resolved : stored;

        private string ToStoredPath(string local)
        {
            if (m_paths == null || local.StartsWith("{"))
                return local;

            try
            {
                return m_paths.MakePortable(local);
            }
            catch (Exception)
            {
                return local;
            }
        }

        public void Add(string path, IProgress<string> progress = null)
        {
            if (!MonitoredFolders.Contains(path))
            {
                MonitoredFolders.Add(path);
                SaveFolderList();
            }

            Select(path);
            // Pass progress down to Create
            Create(path, false, progress);
        }

        public void Select(string path)
        {
            if (m_selectedFolder == path)
                return;

            m_selectedFolder = path;
            m_selectedDatabaseFileName = Path.Combine(path, m_databaseFileName);
            FilesNotMatching.Clear();
        }

        public void Delete(string path)
        {
            var detailFile = Path.Combine(path, m_databaseFileName);
            if (File.Exists(detailFile))
                File.Delete(detailFile);

            var found = MonitoredFolders.IndexOf(path);
            if (found != -1)
                MonitoredFolders.RemoveAt(found);

            SaveFolderList();
        }

        public void Verify(IProgress<string> progress = null)
        {
            FilesNotMatching.Clear();

            progress?.Report($"Scanning: {m_selectedFolder}");
            Create(m_selectedFolder, true, progress);

            progress?.Report("Comparing with database...");

            var detailFile = Path.Combine(m_selectedFolder, m_databaseFileName);
            var savedRecords = new List<Record>();

            if (File.Exists(detailFile))
            {
                var lines = File.ReadAllLines(detailFile);
                savedRecords = lines.Select(Record.FromString).ToList();
            }

            // CORRECCIÓN: Sustituimos ToDictionary por una creación manual segura
            // 1. Usamos StringComparer.OrdinalIgnoreCase porque en Windows "Foto.jpg" es igual a "foto.jpg"
            var savedDict = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in savedRecords)
            {
                // Si el fichero de texto está corrupto y tiene duplicados, 
                // nos quedamos con el primero y descartamos los siguientes para evitar el crash.
                if (!savedDict.ContainsKey(r.Name))
                {
                    savedDict.Add(r.Name, r);
                }
            }

            // Lista temporal para procesar 'Real' vs 'Saved'
            var currentFiles = new List<Record>(m_records);

            // Iteramos sobre lo que acabamos de leer del disco (Lo REAL)
            foreach (var realRecord in currentFiles)
            {
                if (savedDict.TryGetValue(realRecord.Name, out Record savedRecord))
                {
                    // El archivo existe en la BD. Comprobamos si ha cambiado el tamaño.
                    if (realRecord.Size != savedRecord.Size)
                    {
                        realRecord.What = "SizeChanged";
                        FilesNotMatching.Add(realRecord);
                    }

                    // Lo quitamos del diccionario. Lo que sobre en el diccionario al final, son los borrados.
                    savedDict.Remove(realRecord.Name);
                }
                else
                {
                    // No está en la BD -> Es NUEVO
                    realRecord.What = "Added";
                    FilesNotMatching.Add(realRecord);
                }
            }

            // Los que quedan en el diccionario son ficheros que estaban en la BD pero no en el disco -> BORRADOS
            foreach (var deleted in savedDict.Values)
            {
                deleted.What = "Deleted";
                m_records.Add(deleted); // Lo añadimos a m_records para mantener el estado
                FilesNotMatching.Add(deleted);
            }
            FilesNotMatching.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            progress?.Report("Verification complete.");
        }

        internal void Update(IList selectedItems, IProgress<string> progress = null)
        {
            int count = 0;
            int total = selectedItems.Count;

            // Create a copy to avoid modification exceptions if the source collection changes
            var itemsToProcess = selectedItems.Cast<Record>().ToList();

            foreach (var r in itemsToProcess)
            {
                count++;
                progress?.Report($"Updating {count}/{total}: {r.Name}");

                if (r == null) continue;

                Record found;
                bool removeFromList = false;
                // Construct path carefully. 
                // Note: The original logic using ".." seemed to imply relative path handling. 
                // Ensure m_selectedFolder matches the root of r.Name context.
                var info = new FileInfo(Path.Combine(m_selectedFolder, r.Name));

                switch (r.What)
                {
                    case "Added":
                        if (info.Exists)
                        {
                            r.What = "Ok";
                            r.Size = info.Length;
                            File.AppendAllText(Path.Combine(m_selectedFolder, m_databaseFileName), r + Environment.NewLine);
                            m_records.Add(r);
                            removeFromList = true;
                        }
                        break;
                    case "Deleted":
                        found = m_records.FirstOrDefault(i => i.Name == r.Name);
                        if (found != null)
                        {
                            m_records.Remove(found);
                            SaveDetailFileList();
                            removeFromList = true;
                        }
                        break;
                    default: // SizeChanged or others
                        if (info.Exists)
                        {
                            found = m_records.FirstOrDefault(i => i.Name == r.Name);
                            if (found != null)
                            {
                                found.Size = info.Length;
                                found.What = "Ok";
                                SaveDetailFileList();
                                removeFromList = true;
                            }
                        }
                        break;
                }

                if (removeFromList)
                {
                    // Locking not strictly necessary if running on a single background thread 
                    // and not reading FilesNotMatching from UI until finished, but good practice.
                    lock (FilesNotMatching)
                    {
                        FilesNotMatching.Remove(r);
                    }
                }
            }
        }

        private void SaveFolderList()
        {
            // Atomic because the config directory is normally synced: a half-written list would propagate.
            AtomicFile.WriteAllText(m_monitoredFolderList,
                string.Concat(MonitoredFolders.Select(f => ToStoredPath(f) + Environment.NewLine)));
        }

        private void SaveDetailFileList()
        {
            // Performance: If list is huge, StringBuilder or buffered write is better, 
            // but WriteAllLines is acceptable for moderate sizes.
            var lines = m_records.Select(item => item.ToString()).ToList();
            File.WriteAllLines(m_selectedDatabaseFileName, lines);
        }

        // ... [Record class implementation remains the same] ...
        public class Record
        {
            public string Name { get; set; }
            public long Size { get; set; }
            public string What { get; set; }

            public override string ToString()
            {
                return Name + "|" + Size.ToString() + "|" + What;
            }

            public override bool Equals(object obj)
            {
                return obj is Record r && Name == r.Name;
            }

            public override int GetHashCode() => Name?.GetHashCode() ?? 0;

            public static Record FromString(string record)
            {
                var r = new Record();
                var tokens = record.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 3)
                {
                    r.Name = tokens[0];
                    if (long.TryParse(tokens[1], out long size)) r.Size = size;
                    r.What = tokens[2];
                }
                return r;
            }
        }

        private void Create(string path, bool doNotSave, IProgress<string> progress)
        {
            m_records.Clear();
            RecursiveLoad(path, path, progress);

            if (doNotSave) return;

            var detailFile = Path.Combine(path, m_databaseFileName);
            if (File.Exists(detailFile))
                File.Delete(detailFile);

            SaveDetailFileList();
        }

        private void RecursiveLoad(string path, string basePath, IProgress<string> progress)
        {
            // OPTIMIZACIÓN 2: Pre-calcular la longitud base para hacer substring rápido
            int basePathLen = basePath.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? basePath.Length
                : basePath.Length + 1;

            var stack = new Stack<string>();
            stack.Push(path);

            while (stack.Count > 0)
            {
                var currentDir = stack.Pop();

                try
                {
                    var directoryInfo = new DirectoryInfo(currentDir);

                    // Usamos EnumerateFileSystemInfos para obtener Ficheros y Directorios en una sola pasada (si es posible)
                    // o EnumerateFiles que devuelve FileInfo directamente con los datos ya cargados.

                    foreach (var fileInfo in directoryInfo.EnumerateFiles())
                    {
                        if (IsIgnoredName(fileInfo.Name))
                            continue;

                        // OPTIMIZACIÓN 3: Lógica de strings simple en lugar de Uri
                        string relativePath = fileInfo.FullName.Substring(basePathLen);

                        m_records.Add(new Record
                        {
                            Name = relativePath,
                            Size = fileInfo.Length,
                            What = "Ok"
                        });
                    }

                    foreach (var dirInfo in directoryInfo.EnumerateDirectories())
                    {
                        if (IsIgnoredName(dirInfo.Name))
                            continue;

                        stack.Push(dirInfo.FullName);
                    }

                    // Reportar progreso cada X carpetas o simplemente la actual
                    // Nota: Demasiados reportes pueden ralentizar la UI, úsalo con mesura si hay muchas subcarpetas.
                    // progress?.Report($"Scanning: {currentDir}"); 
                }
                catch (System.Exception e)
                {
                    // Capturamos UnauthorizedAccessException, etc.
                    Console.WriteLine($"Skipping {currentDir}: {e.Message}");
                }
            }
        }

        // OPTIMIZACIÓN 3 (Detalle): Eliminamos la dependencia de Uri
        // Ya no necesitamos MakeRelative como método público complejo, 
        // la lógica está integrada en el bucle con Substring, que es mucho más rápido.
        // Si necesitas mantener el método por compatibilidad:
        public static string MakeRelative(string filePath, string referencePath)
        {
            if (filePath.StartsWith(referencePath, StringComparison.OrdinalIgnoreCase))
            {
                int len = referencePath.Length;
                if (!referencePath.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    len++;

                return filePath.Substring(len);
            }
            return filePath;
        }

        private static bool IsIgnoredName(string fileName)
        {
            // Pequeña optimización: comprobar el primer carácter antes de hacer strings completos si es posible,
            // pero StartsWith ya es bastante eficiente.
            return fileName.StartsWith(".") || fileName.StartsWith("$") || fileName.Equals(m_databaseFileName, StringComparison.OrdinalIgnoreCase);
        }
    }
}