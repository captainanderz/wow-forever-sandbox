-- Forever Sandbox client-side helpers, using only the game's own Lua API.
-- /fog off|on               the client's own fog settings (see below)
-- /walls [on|off|overlay]   impassable terrain walls: forwards to the sandbox server's .walls command
-- /sandbox cvars [filter]   lists settings (CVars) and console commands whose name or help text contains filter (default "fog")
-- Every setting (value, default, flags) and console command is also saved to SandboxToolsDump (WTF\Account\...\SavedVariables)
-- at login and logout, so the full list can be read outside the game.

local function AllCommands()
    if C_Console and C_Console.GetAllCommands then return C_Console.GetAllCommands() end
    if ConsoleGetAllCommands then return ConsoleGetAllCommands() end
    return {}
end

local CVAR_TYPE = Enum and Enum.ConsoleCommandType and Enum.ConsoleCommandType.Cvar or 0

local function Dump()
    local version, build, date, toc = GetBuildInfo()
    local out = { version = version, build = build, date = date, interface = toc, cvars = {}, commands = {} }
    for _, c in ipairs(AllCommands()) do
        if c.commandType == CVAR_TYPE then
            local value, default, account, character, locked, secure, readOnly
            if C_CVar and C_CVar.GetCVarInfo then
                value, default, account, character, locked, secure, readOnly = C_CVar.GetCVarInfo(c.command)
            else
                value = GetCVar(c.command)
            end
            out.cvars[#out.cvars + 1] = { name = c.command, value = value, default = default, help = c.help, category = c.category,
                locked = locked, secure = secure, readOnly = readOnly }
        else
            out.commands[#out.commands + 1] = { name = c.command, help = c.help, category = c.category, type = c.commandType }
        end
    end
    SandboxToolsDump = out
    return out
end

local function Matches(entry, filter)
    return entry.name:lower():find(filter, 1, true) or (entry.help or ""):lower():find(filter, 1, true)
end

SLASH_SANDBOXTOOLS1 = "/sandbox"
SlashCmdList.SANDBOXTOOLS = function(message)
    local command, rest = (message or ""):match("^(%S*)%s*(.-)$")
    if command ~= "" and command ~= "cvars" then
        print("Sandbox Tools: /sandbox cvars [filter]")
        return
    end
    local filter = (rest ~= "" and rest or "fog"):lower()
    local dump, count = Dump(), 0
    for _, v in ipairs(dump.cvars) do
        if Matches(v, filter) then
            count = count + 1
            print(("|cff33ff99%s|r = %s (default %s)%s%s"):format(v.name, tostring(v.value), tostring(v.default),
                v.readOnly and " [read-only]" or "", v.help and v.help ~= "" and (" - " .. v.help) or ""))
        end
    end
    for _, v in ipairs(dump.commands) do
        if Matches(v, filter) then
            count = count + 1
            print(("|cffffcc00/console %s|r%s"):format(v.name, v.help and v.help ~= "" and (" - " .. v.help) or ""))
        end
    end
    print(("Sandbox Tools: %d matches for '%s' (%d settings, %d commands; the full list is saved at logout)."):format(count, filter, #dump.cvars, #dump.commands))
end

-- /fog off|on (no argument toggles): the client's own fog settings. Off disables the "full fog" distance band
-- (disableHorizonStart, horizonStart moved out to the horizon clip) and volumetric fog outdoors and indoors. The previous
-- values are kept in SandboxToolsSettings and restored by /fog on; the game saves the settings itself, so the choice persists.
-- ForeverSandbox.exe watches the volumetric-fog setting: while it is off, the launcher also clears the map's own fog and the
-- scene fog switch in the game's memory (no setting reaches those), and restores them when it is back on.
local FOG_SETTINGS = { "disableHorizonStart", "horizonStart", "volumeFog", "volumeFogInterior" }

local function SetSetting(name, value)
    if C_CVar and C_CVar.SetCVar then return C_CVar.SetCVar(name, value) end
    return SetCVar(name, value)
end

SLASH_SANDBOXFOG1 = "/fog"
SlashCmdList.SANDBOXFOG = function(message)
    SandboxToolsSettings = SandboxToolsSettings or {}
    local settings = SandboxToolsSettings
    local choice = (message or ""):lower():match("^%s*(%S*)")
    local off
    if choice == "off" or choice == "0" then off = true
    elseif choice == "on" or choice == "1" then off = false
    elseif choice == "" then off = not settings.fogOff
    else print("Sandbox Tools: /fog off | /fog on") return end

    if off then
        if not settings.fogOff then
            settings.savedFog = {}
            for _, name in ipairs(FOG_SETTINGS) do settings.savedFog[name] = GetCVar(name) end
        end
        SetSetting("disableHorizonStart", "1")
        SetSetting("horizonStart", GetCVar("horizonClip"))
        SetSetting("volumeFog", "0")
        SetSetting("volumeFogInterior", "0")
        settings.fogOff = true
        print("Sandbox: fog off (distance band, volumetric, map and scene fog). /fog on brings it back.")
    else
        for name, value in pairs(settings.savedFog or {}) do SetSetting(name, value) end
        settings.fogOff, settings.savedFog = false, nil
        print("Sandbox: fog back on (your previous settings restored).")
    end
end

-- /walls: the server draws impassable terrain walls (MCNK impass flag) as game objects, or the launcher as an overlay.
-- The server answers dot commands in chat itself; the slash command only saves typing the dot.
SLASH_SANDBOXWALLS1 = "/walls"
SlashCmdList.SANDBOXWALLS = function(message)
    local args = (message or ""):match("^%s*(.-)%s*$")
    SendChatMessage(args == "" and ".walls" or (".walls " .. args), "SAY")
end

local frame = CreateFrame("Frame")
frame:RegisterEvent("PLAYER_LOGIN")
frame:RegisterEvent("PLAYER_LOGOUT")
frame:SetScript("OnEvent", function(_, event)
    local dump = Dump()
    if event == "PLAYER_LOGIN" then
        local fogOff = SandboxToolsSettings and SandboxToolsSettings.fogOff
        print(("Sandbox Tools loaded: /fog %s, /walls, /sandbox cvars <filter>."):format(fogOff and "on (fog is off now)" or "off"))
    end
end)
