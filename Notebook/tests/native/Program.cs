using System.Text;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using WinAssistant.Notebook;

namespace WinAssistant.Settings
{
    // Only the host theme dependency is substituted. All file operations under
    // test use the actual production service, not a mock filesystem.
    public sealed class ConfigurationStore
    {
        public static ConfigurationStore Shared { get; } = new();
        public Config Configuration { get; } = new();
        public sealed class Config { public AudioConfig Audio { get; } = new(); }
        public sealed class AudioConfig { public string Theme { get; } = "Light"; }
    }
}
internal static class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length == 2) { ValidateAssets(args[0], args[1]); return; }
        var temp = Path.Combine(Path.GetTempPath(), "notebook-native-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        try
        {
            var root = Path.Combine(temp,"library"); var service = new NotebookService(Path.Combine(temp,"config"),root);
            JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
            async Task Write(string path,string value) => await service.ExecuteAsync("write",Args(new { path,data=Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) }));
            async Task<string?> Read(string path) => (string?)await service.ExecuteAsync("read",Args(new{path}));
            void Check(bool value,string message) { if(!value)throw new Exception(message); Console.WriteLine("PASS " + message); }
            // A remote request must bypass a busy file queue. Validation fails
            // before credentials/network access, so this runs on Linux too.
            var fileGate = (SemaphoreSlim)typeof(NotebookService).GetField("gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!;
            await fileGate.WaitAsync();
            var remote = service.ExecuteAsync("http", Args(new { method = "PATCH", path = "" }));
            try { Check(await Task.WhenAny(remote, Task.Delay(1000)) == remote, "WebDAV request does not wait for the local file gate"); }
            finally { fileGate.Release(); }
            try { await remote; throw new Exception("invalid remote method accepted"); }
            catch (IOException) { Console.WriteLine("PASS remote validation remains enforced"); }
            await Write("books/note/main.md","first");await Write("books/note/main.md","second");
            Check(await Read("books/note/main.md")=="second","atomic replacement retains new Markdown");
            Check(File.ReadAllText(Path.Combine(root,"books/note/main.md.bak"))=="first","previous Markdown remains in backup");
            await Write("meta.json","{\"version\":1}");await Write("meta.json","{\"version\":2}");File.WriteAllText(Path.Combine(root,"meta.json"),"{broken");
            Check(await Read("meta.json")=="{\"version\":1}","damaged JSON restores valid backup");
            Check(File.ReadAllText(Path.Combine(root,"meta.json"))=="{\"version\":1}","backup recovery repairs primary atomically");
            foreach(var path in new[]{"../outside","books/../../outside","/tmp/outside","a\\b"}){try{service.Resolve(path);throw new Exception("scope accepted "+path);}catch(IOException){}}
            Console.WriteLine("PASS path traversal rejected");
            Directory.CreateSymbolicLink(Path.Combine(root,"linked"),temp);try{service.Resolve("linked/file");throw new Exception("symlink accepted");}catch(IOException){}File.Delete(Path.Combine(root,"linked"));
            Console.WriteLine("PASS symbolic links rejected");
            await service.ExecuteAsync("mkdir",Args(new{path="books/note/reference"}));await Write("books/note/reference/file.bin","attachment");
            await service.ExecuteAsync("move",Args(new{path="books/note/reference/file.bin",to="books/note/.trash/file.bin"}));
            Check(await Read("books/note/.trash/file.bin")=="attachment","attachment move preserves bytes");
            await service.ExecuteAsync("move",Args(new{path="books/note/.trash/file.bin",to="books/note/reference/file.bin"}));
            Check(await Read("books/note/reference/file.bin")=="attachment","attachment restoration preserves bytes");
            var exported = Path.Combine(temp,"另存为 (PDF).bin");
            service.ExportAttachment("books/note/reference/file.bin", exported);
            Check(File.ReadAllText(exported)=="attachment", "attachment export preserves bytes");
            File.WriteAllText(exported,"old export"); service.ExportAttachment("books/note/reference/file.bin", exported);
            Check(File.ReadAllText(exported)=="attachment" && await Read("books/note/reference/file.bin")=="attachment", "export replaces destination without moving original");
            await Write("books/note/reference/file.bin","updated");await service.ExecuteAsync("remove",Args(new{path="books/note/reference/file.bin"}));
            Check(!File.Exists(Path.Combine(root,"books/note/reference/file.bin.bak")),"permanent cleanup removes the matching backup");
            try{await service.ExecuteAsync("remove",Args(new{path="books/note"}));throw new Exception("recursive removal accepted");}catch(IOException){}
            Console.WriteLine("PASS recursive permanent directory removal rejected");
            try{await service.ExecuteAsync("purgeDeleted",Args(new{path="books/note"}));throw new Exception("live purge accepted");}catch(IOException){}
            Check(await Read("books/note/main.md")=="second","recycle purge rejects live notes");
            await Write(".deleted/archived/main.md","archived");await Write(".deleted/archived/reference/file.bin","bytes");
            await service.ExecuteAsync("purgeDeleted",Args(new{path=".deleted/archived"}));
            Check(!Directory.Exists(Path.Combine(root,".deleted/archived")),"recycle purge removes archived note and attachments");
            await Write(".deleted-books/archive/book.json","{}");Directory.CreateSymbolicLink(Path.Combine(root,".deleted-books/archive/link"),temp);
            try{await service.ExecuteAsync("purgeDeleted",Args(new{path=".deleted-books/archive"}));throw new Exception("linked purge accepted");}catch(IOException){}
            Check(File.Exists(Path.Combine(root,".deleted-books/archive/book.json")),"recycle purge rejects symlinks before deleting files");
            File.Delete(Path.Combine(root,".deleted-books/archive/link"));
            var target=Path.Combine(temp,"migrated");Directory.CreateDirectory(target);await service.MigrateAsync(target);
            Check(File.ReadAllText(Path.Combine(target,"books/note/main.md"))=="second","migration copies and verifies notes");
            Check(File.Exists(Path.Combine(root,"books/note/main.md")),"migration retains original library");
            var occupied=Path.Combine(temp,"occupied");Directory.CreateDirectory(occupied);File.WriteAllText(Path.Combine(occupied,"user.txt"),"keep");
            try{await service.MigrateAsync(occupied);throw new Exception("occupied target accepted");}catch(IOException){}
            Check(File.ReadAllText(Path.Combine(occupied,"user.txt"))=="keep","nonempty migration target preserved");
        }
        finally { Directory.Delete(temp,true); }
    }
    static void ValidateAssets(string assembly, string sourceFolder)
    {
        using var stream = File.OpenRead(assembly);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var bytes = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress).GetContent().ToArray();
        var count = 0;
        var headerChecked = false;
        var windowIconChecked = false;
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            var name = metadata.GetString(resource.Name);
            if (name.EndsWith(".g.resources", StringComparison.Ordinal)) {
                var resourceOffset = checked((int)resource.Offset);
                var resourceLength = BitConverter.ToInt32(bytes, resourceOffset);
                using var reader = new System.Resources.ResourceReader(new MemoryStream(bytes, resourceOffset + 4, resourceLength));
                reader.GetResourceData("resources/appheader.png", out var type, out var data);
                var png = data.AsSpan(4, BitConverter.ToInt32(data, 0));
                var original = File.ReadAllBytes(Path.Combine(sourceFolder, "../../MACAssistant/Assets.xcassets/AppIcon.appiconset/AppIcon-128.png"));
                if (!png.SequenceEqual(original)) throw new Exception("High-resolution title-bar icon is missing or differs from source");
                headerChecked = true;
                reader.GetResourceData("resources/appicon.ico", out _, out var iconData);
                var icon = iconData.AsSpan(4, BitConverter.ToInt32(iconData, 0));
                var iconSource = File.ReadAllBytes(Path.Combine(sourceFolder, "../../WinAssistant/Resources/AppIcon.ico"));
                if (!icon.SequenceEqual(iconSource)) throw new Exception("Native taskbar ICO is missing or differs from source");
                var frames = BitConverter.ToUInt16(iconSource, 4);
                var sizes = Enumerable.Range(0, frames).Select(i => iconSource[6 + i * 16] == 0 ? 256 : (int)iconSource[6 + i * 16]).ToArray();
                if (!sizes.Contains(32) || !sizes.Contains(256)) throw new Exception("Native taskbar ICO needs small and high-resolution frames");
                windowIconChecked = true;
            }
            if (!name.StartsWith("NotebookWeb/", StringComparison.Ordinal)) continue;
            if (!resource.Implementation.IsNil) throw new Exception("Notebook resource is not embedded: " + name);
            var offset = checked((int)resource.Offset);
            var length = BitConverter.ToInt32(bytes, offset);
            var embedded = bytes.AsSpan(offset + 4, length);
            var file = Path.Combine(sourceFolder, name["NotebookWeb/".Length..].Replace('\\', '/'));
            if (!SHA256.HashData(embedded).SequenceEqual(SHA256.HashData(File.ReadAllBytes(file)))) throw new Exception("Resource mismatch: " + name);
            count++;
        }
        var expected = Directory.GetFiles(sourceFolder, "*", SearchOption.AllDirectories).Length;
        if (count != expected || count < 20) throw new Exception($"Incomplete offline editor resources: {count}/{expected}");
        Console.WriteLine($"PASS {count} embedded offline resources match source SHA-256");
        if (!headerChecked) throw new Exception("Title-bar PNG resource was not packaged");
        Console.WriteLine("PASS high-resolution title-bar PNG is packaged at the expected WPF URI");
        if (!windowIconChecked) throw new Exception("Native taskbar ICO resource was not packaged");
        Console.WriteLine("PASS native taskbar ICO matches source with 32px and 256px frames");
    }

}
