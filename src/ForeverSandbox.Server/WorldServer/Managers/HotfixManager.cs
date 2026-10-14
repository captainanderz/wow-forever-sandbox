using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ForeverSandbox.Server.WorldServer.Game.Entities;
using AuthServer.Network;
using AuthServer.WorldServer.Managers;

using Framework.Constants.Misc;
using Framework.Constants.Net;
using Framework.Logging;
using Framework.Misc;
using Framework.Network.Packets;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ForeverSandbox.Server.WorldServer.Managers
{
    public class HotfixManager : Singleton<HotfixManager>
    {
        const string HotfixFolder = "hotfixes";
        const string HotfixFileExtension = ".json";

        public Dictionary<uint, Dictionary<int, byte[]>> Hotfixes { get; }

        public Dictionary<int, (uint UniqueID, uint TableHash, int RecordID)> Pushes { get; private set; } = [];

        private List<int> ChangedPushesSinceLastHotfixMessage = [];

        private FileSystemWatcher hotfixDirWatcher;

        private int lastPushID;

        HotfixManager()
        {
            Hotfixes = [];

            if (!Directory.Exists("./hotfixes"))
                Directory.CreateDirectory("./hotfixes");

            hotfixDirWatcher = new FileSystemWatcher
            {
                Path = "./hotfixes",
                NotifyFilter = NotifyFilters.LastWrite,
                Filter = "*.json",
                EnableRaisingEvents = true,
                IncludeSubdirectories = false
            };

            if (File.Exists("lastPushID.txt"))
                lastPushID = int.Parse(File.ReadAllText("lastPushID.txt"));
            else
            {
                lastPushID = 1;
                File.WriteAllText("lastPushID.txt", lastPushID.ToString());
            }

            hotfixDirWatcher.Changed += new FileSystemEventHandler(OnHotfixChanged);
        }

        private void OnHotfixChanged(object sender, FileSystemEventArgs e)
        {
            Log.Message(LogType.Info, "Hotfixes changed for " + Path.GetFileNameWithoutExtension(e.FullPath) + ": " + e.ChangeType);

            try
            {
                LoadHotfixJSON(e.FullPath).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch(IOException ioEx)
            {
                Log.Message(LogType.Info, $"{e.FullPath} is currently busy/locked, but we should be trying again.");
            }
            catch (Exception ex)
            {
                Log.Message(LogType.Error, $"Error reading hotfixes from {e.FullPath}, make sure it is valid JSON: {ex.Message}");
                return;
            }

            for (int i = 0; i < Manager.WorldMgr.Sessions.Count; i++)
            {
                SendAvailableHotfixes(Manager.WorldMgr.Sessions.ElementAt(i).Value);
            }
        }

        public async Task Load()
        {
            Log.Message(LogType.Info, $"Loading hotfixes...");

            foreach (var f in Directory.GetFiles($"{HotfixFolder}", $"*{HotfixFileExtension}"))
            {
                try
                {
                    await LoadHotfixJSON(f);
                }
                catch (Exception e)
                {
                    Log.Message(LogType.Error, $"Error reading hotfixes from {f}, make sure it is valid JSON: {e.Message}");
                    continue;
                }
            }
        }

        public async Task LoadHotfixJSON(string filename)
        {
            var hotfixName = Path.GetFileNameWithoutExtension(filename).ToLowerInvariant();

            var fileContent = await File.ReadAllTextAsync(filename);

            if (fileContent.Length == 0)
                return;

            dynamic hotfixObject = JsonConvert.DeserializeObject(fileContent);

            if (!Manager.WorldMgr.DBInfo.TryGetValue(hotfixName, out var dbInfo))
            {
                Log.Message(LogType.Error, "Tried to read hotfix file with name " + hotfixName + " which is not a known table, skipping!");
                return;
            }

            Hotfixes[dbInfo.TableHash] = [];

            foreach (JObject entry in hotfixObject)
            {
                var fields = new List<string>();
                var properties = entry.Properties().Skip(1);

                foreach (JProperty property in properties)
                {
                    if (property.Value is JArray items)
                    {
                        foreach (var item in items)
                            fields.Add(item.ToString());
                    }
                    else
                        fields.Add(property.Value.ToString());
                }

                var recordID = uint.Parse(entry.Property("Id").Value.ToString());

                if (!dbInfo.HasIndex)
                    fields.Insert(dbInfo.IDPosition, recordID.ToString());

                // Write the hotfix row data.
                using (var ms = new MemoryStream())
                using (var hotfixRow = new BinaryWriter(ms))
                {
                    for (var i = 0; i < fields.Count; i++)
                    {
                        switch (dbInfo.FieldTypes[i].ToLower())
                        {
                            case "string":
                                var sBytes = Encoding.UTF8.GetBytes(fields[i]);

                                hotfixRow.Write(sBytes, 0, sBytes.Length);
                                hotfixRow.Write((byte)0);
                                break;
                            case "sbyte":
                                {
                                    if (sbyte.TryParse(fields[i], out var signedResult))
                                        hotfixRow.Write(signedResult);
                                    else if (byte.TryParse(fields[i], out var unsignedResult))
                                        hotfixRow.Write(unsignedResult);
                                    else
                                        throw new InvalidDataException("sbyte || byte");

                                    break;
                                }
                            case "byte":
                                {
                                    if (byte.TryParse(fields[i], out var unsignedResult))
                                        hotfixRow.Write(unsignedResult);
                                    else if (sbyte.TryParse(fields[i], out var signedResult))
                                        hotfixRow.Write(signedResult);
                                    else
                                        throw new InvalidDataException("sbyte || byte");

                                    break;
                                }
                            case "int16":
                                {
                                    if (short.TryParse(fields[i], out var signedResult))
                                        hotfixRow.Write(signedResult);
                                    else if (ushort.TryParse(fields[i], out var unsignedResult))
                                        hotfixRow.Write(unsignedResult);
                                    else
                                        throw new InvalidDataException("int16 || uint16");

                                    break;
                                }
                            case "uint16":
                                {
                                    if (ushort.TryParse(fields[i], out var unsignedResult))
                                        hotfixRow.Write(unsignedResult);
                                    else if (short.TryParse(fields[i], out var signedResult))
                                        hotfixRow.Write(signedResult);
                                    else
                                        throw new InvalidDataException("int16 || uint16");

                                    break;
                                }
                            case "int32":
                                {
                                    if (int.TryParse(fields[i], out var signedResult))
                                        hotfixRow.Write(signedResult);
                                    else if (uint.TryParse(fields[i], out var unsignedResult))
                                        hotfixRow.Write(unsignedResult);
                                    else
                                        throw new InvalidDataException("int32 || uint32");

                                    break;
                                }
                            case "uint32":
                                {
                                    if (uint.TryParse(fields[i], out var unsignedResult))
                                        hotfixRow.Write(unsignedResult);
                                    else if (int.TryParse(fields[i], out var signedResult))
                                        hotfixRow.Write(signedResult);
                                    else
                                        throw new InvalidDataException("int32 || uint32");

                                    break;
                                }
                            case "single":
                                hotfixRow.Write(float.Parse(fields[i], NumberStyles.Any, CultureInfo.InvariantCulture));
                                break;
                            case "int64":
                                hotfixRow.Write(long.Parse(fields[i]));
                                break;
                            case "uint64":
                                hotfixRow.Write(ulong.Parse(fields[i]));
                                break;
                            case "ref":
                                hotfixRow.Write(uint.Parse(fields[i]));
                                break;
                            default:
                                Log.Message(LogType.Error, "Unknown field type for hotfixes.");
                                break;
                        }
                    }

                    var hotfixData = ms.ToArray();
                    Hotfixes[dbInfo.TableHash].Add((int)recordID, hotfixData);

                    var crc = BitConverter.ToUInt32(System.IO.Hashing.Crc32.Hash(hotfixData));

                    // Check if this hotfix already exists in the current pushes
                    if (Pushes.Any(x => x.Value.TableHash == dbInfo.TableHash && x.Value.RecordID == recordID && x.Value.UniqueID == crc))
                    {
                        Log.Message(LogType.Info, $"Hotfix for {hotfixName} with record ID {recordID} and the same data already exists, skipping..");
                        continue;
                    }
                    else
                    {
                        // Remove any existing pushes with this table hash and record ID
                        foreach (var push in Pushes.Where(x => x.Value.TableHash == dbInfo.TableHash && x.Value.RecordID == recordID))
                            Pushes.Remove(push.Key);

                        Pushes.Add(lastPushID++, (crc, dbInfo.TableHash, (int)recordID));

                        ChangedPushesSinceLastHotfixMessage.Add(lastPushID - 1);
                    }

                }
            }
        }

        public void Clear()
        {
            Hotfixes.Clear();
        }

        public void SendClearHotfixes(WorldClass session)
        {
            Log.Message(LogType.Info, $"Cleaning hotfix cache...");

            foreach (var table in Hotfixes)
            {
                var hotfixMessage = new PacketWriter(ServerMessage.HotfixMessage);

                hotfixMessage.WriteInt32(table.Value.Count);

                foreach (var hotfix in table.Value)
                {
                    hotfixMessage.Write(hotfix.Key + 838926338);
                    hotfixMessage.Write(hotfix.Key + 838926338);
                    hotfixMessage.Write(table.Key);
                    hotfixMessage.Write(hotfix.Key);
                    hotfixMessage.Write(0);
                    hotfixMessage.Write((byte)0); // allow
                }

                hotfixMessage.Write(0);

                session.Send(ref hotfixMessage);
            }

            Log.Message(LogType.Info, $"Done");
        }

        public void SendAvailableHotfixes(WorldClass session)
        {
            Log.Message(LogType.Info, $"Sending available hotfixes...");

            var availableHotfixes = new PacketWriter(ServerMessage.AvailableHotfixes);

            availableHotfixes.WriteInt32(838926338);    // Virtual Realm Address
            availableHotfixes.WriteInt32(Pushes.Count); // Total hotfix count

            foreach (var push in Pushes)
            {
                availableHotfixes.WriteInt32(push.Key);                // PushID
                availableHotfixes.WriteUInt32(push.Value.UniqueID);     // UniqueID
            }

            session.Send(ref availableHotfixes);

            Log.Message(LogType.Info, $"Done");
        }

        public void SendDBReply(WorldClass session, int pushID)
        {
            if (Pushes.TryGetValue(pushID, out var pushInfo) && Hotfixes.TryGetValue(pushInfo.TableHash, out var tableRows) && tableRows.TryGetValue(pushInfo.RecordID, out var hotfixRow))
            {
                var dbReply = new PacketWriter(ServerMessage.DBReply);

                dbReply.Write(pushInfo.TableHash);
                dbReply.Write(pushInfo.RecordID);
                dbReply.WriteUInt32((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                dbReply.Write((byte)0x80); // Status? AKA 1,2,3 or 4? Or something else?

                dbReply.Write(hotfixRow.Length);
                dbReply.Write(hotfixRow);

                session.Send(ref dbReply);
            }
        }

        public void SendHotfixMessage(WorldClass session, List<int> RequestedPushIDs)
        {
            Log.Message(LogType.Info, $"Sending hotfixes for requested pushes: " + string.Join(", ", RequestedPushIDs) + "...");

            var hotfixMessage = new PacketWriter(ServerMessage.HotfixMessage);

            var pushIDCountPos = hotfixMessage.BaseStream.Position;

            hotfixMessage.WriteInt32(RequestedPushIDs.Count);
            var bitPack = new BitPack(hotfixMessage);

            var availablePushes = 0;
            using (var hotfixData = new MemoryStream())
            {
                foreach (var pushID in RequestedPushIDs)
                {
                    if (!ChangedPushesSinceLastHotfixMessage.Contains(pushID))
                    {
                        Log.Message(LogType.Error, $"Client requested hotfix for {pushID}, but it was not changed since last hotfix message, skipping..");
                        continue;
                    }

                    if (Pushes.TryGetValue(pushID, out var pushInfo) && Hotfixes.TryGetValue(pushInfo.TableHash, out var tableRows) && tableRows.TryGetValue(pushInfo.RecordID, out var hotfixRow))
                    {
                        var startPos = hotfixMessage.BaseStream.Position;

                        hotfixMessage.Write(pushID);            // Push ID
                        hotfixMessage.Write(pushInfo.UniqueID); // Push Unique ID
                        hotfixMessage.Write(pushInfo.TableHash);// Table Hash 
                        hotfixMessage.Write(pushInfo.RecordID); // Record ID
                        hotfixMessage.Write(hotfixRow.Length);  // Hotfix data length
                        bitPack.Write(1, 3);                    // Hotfix status
                        bitPack.Flush();

                        //Log.Message(LogType.Debug, $"Appending hotfix for pushID {pushID} with Unique ID {pushInfo.UniqueID}, table hash {pushInfo.TableHash} and record ID {pushInfo.RecordID} (started at {startPos}, currently at {hotfixMessage.BaseStream.Position})");

                        // Write the hotfix data to the stream.
                        hotfixData.Write(hotfixRow);

                        availablePushes++;
                    }
                    else
                    {
                        Log.Message(LogType.Error, $"Client requested hotfix for {pushID}, but it or a connected hotfix record could not be found, skipping..");
                    }
                }

                // Write actual pushID count in reply in header
                var prevPos = hotfixMessage.BaseStream.Position;
                hotfixMessage.BaseStream.Position = pushIDCountPos;
                hotfixMessage.Write(availablePushes);
                hotfixMessage.BaseStream.Position = prevPos;

                var hotfixDataArray = hotfixData.ToArray();

                hotfixMessage.Write(hotfixDataArray.Length);
                hotfixMessage.Write(hotfixDataArray);
            }

            Log.Message(LogType.Debug, $"Sent {availablePushes} to client");

            session.Send(ref hotfixMessage);

            ChangedPushesSinceLastHotfixMessage.Clear();
        }

        public Dictionary<uint, GameObjectDisplayInfo> GameObjectFileHotfixes = new Dictionary<uint, GameObjectDisplayInfo>();

        public void CreateGameObjectDisplayEntry(WorldClass session)
        {
            // Just send all hotfixes.
            foreach (var go in GameObjectFileHotfixes)
            {
                var hotfixMessage = new PacketWriter(ServerMessage.HotfixMessage);

                hotfixMessage.WriteInt32(GameObjectFileHotfixes.Count);

                var hotfixData = new BinaryWriter(new MemoryStream());

                // Write the hotfix row data.
                var hotfixRow = new BinaryWriter(new MemoryStream());

                foreach (var gb in go.Value.GetBox)
                    hotfixRow.Write(gb);

                hotfixRow.Write(go.Value.FileId);
                hotfixRow.Write(go.Value.ObjectEffectPackageID);
                hotfixRow.Write(go.Value.OverrideLootEffectScale);
                hotfixRow.Write(go.Value.OverrideNameScale);

                var hotfixRowData = (hotfixRow.BaseStream as MemoryStream).ToArray();
                var bitPack = new BitPack(hotfixMessage);

                hotfixMessage.Write(-1);
                hotfixMessage.Write(-1);
                hotfixMessage.Write(1829768651);
                hotfixMessage.Write(go.Value.Id);
                hotfixMessage.Write(hotfixRowData.Length);

                // Allow the hotfix.
                // 0 - Invalid, 1 - Valid, 2 - ???, 3 - Skip
                bitPack.Write(1, 3);
                bitPack.Flush();

                // Write the hotfix data to the stream.
                hotfixData.Write(hotfixRowData);

                var hotfixDataArray = (hotfixData.BaseStream as MemoryStream).ToArray();

                hotfixMessage.Write(hotfixDataArray.Length);
                hotfixMessage.Write(hotfixDataArray);

                session.Send(ref hotfixMessage);
            }
        }
    }
}
