// Sandbox-made client files for .walls, shared by ForeverSnapshot (which builds them into the snapshot: `walls install`) and the
// server (which offers GameObjectDisplayInfo rows for them as hotfixes and places them). FileDataIDs and display IDs are far above
// anything the 70338 client has (FileDataIDs end near 7.2 million, GameObjectDisplayInfo IDs at 133941).
namespace AuthServer.Network;

internal static class ForeverWallModels
{
    public const int CopyFile = 9800001, SkinFile = 9800002;
    public const int CopySource = 200328;   // world/generic/quilboar/activedoodads/doors/razorfenforcefield01.m2 (display 523)
    // Display IDs: 900001 = the client's own force field model (tests the hotfix row), 900002 = its copy in the snapshot (tests the
    // added storage); then one yellow and one red wall model per glass tint.
    public const int OriginalDisplay = 900001, CopyDisplay = 900002;
    public const float Width = 100f / 3, Height = 10000f, Bar = 0.35f;
    public const string Mount = "0000000200000000";   // vfs-root mount for enUS files without content flags (where the files go)

    // Glass tints of the wall faces: name, face alpha of yellow and red walls, and how far the face colour is lightened towards
    // white. "faint" is the wowmap viewer's own look (alphas 0.059 / 0.069).
    public static readonly (string Name, float Yellow, float Red, float Lighten)[] Tints =
        { ("faint", 0.059f, 0.069f, 0f), ("light", 0.12f, 0.13f, 0.2f), ("medium", 0.22f, 0.24f, 0.3f), ("strong", 0.35f, 0.38f, 0.35f) };
    public const int DefaultTint = 3;   // strong (the user's choice)
    public static int ModelFile(int tint, bool red) => 9800003 + tint * 2 + (red ? 1 : 0);
    public static int Display(int tint, bool red) => 900003 + tint * 2 + (red ? 1 : 0);

    // Whether `walls install` put every wall model into this client (its record in Data\sandbox-files.json). The server's own storage
    // reader follows the build config's root line, which the install leaves alone, so it does not see the files itself.
    public static bool InstalledIn(string clientInstall)
    {
        try
        {
            var state = System.IO.Path.Combine(clientInstall, "Data", "sandbox-files.json");
            if (!System.IO.File.Exists(state)) return false;
            using var json = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(state));
            var ids = new System.Collections.Generic.HashSet<int>();
            foreach (var id in json.RootElement.GetProperty("FileDataIds").EnumerateArray()) ids.Add(id.GetInt32());
            for (var tint = 0; tint < Tints.Length; tint++)
                if (!ids.Contains(ModelFile(tint, false)) || !ids.Contains(ModelFile(tint, true))) return false;
            return true;
        }
        catch (System.Exception error) when (error is System.IO.IOException or System.Text.Json.JsonException or System.Collections.Generic.KeyNotFoundException or System.InvalidOperationException)
        {
            return false;
        }
    }
    public static bool Installed { get; set; }   // set by the server at start (InstalledIn)
}
