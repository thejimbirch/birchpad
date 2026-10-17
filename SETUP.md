# Setup with Ghostty and herdr

This guide installs the Birchpad plugins for use, not development, with Ghostty and herdr as the terminal.
iTerm2 is not required.

## How it fits together

- The `.lp5` files in `profiles/generated/` are key layouts only.
  Every key points at an action inside a plugin, so the plugins (`.lplug4` packages) must be installed before a profile is useful.
- The Herdr plugin talks to herdr's socket, not to the terminal, and brings forward whichever app runs herdr (Ghostty here).
- Agent Hub fully supports agents in herdr panes: focus, status, reading the approval menu, Allow and Deny, slash commands, and prompts.
- An agent started directly in a Ghostty tab, outside herdr, works too: its tile brings the tab forward, and its page sends slash commands, prompts, number keys, and Esc to it.
  The keypad finds the tab by its folder, so with two Ghostty tabs in one folder (say a shell and `claude`) it picks the first one.
  Its page cannot show the waiting menu's own options, which are read from herdr panes only; it offers Allow, Always, and Deny for a held request, or number keys 1 to 3 and Esc.

Agents in herdr get the most from the keypad; a plain Ghostty tab is fine for the occasional one.

## Which plugins

| Plugin | Needed |
| --- | --- |
| Agent Hub | Required; the Agents profile and the Claude and Codex plugins place its keys |
| Herdr | Required; agents in herdr panes and the Workspaces key |
| Claude | Optional; a home page when Claude Desktop is in front |
| Codex | Optional; a home page when the ChatGPT app is in front |
| iTerm | Skip |

The profile to use is **Birchpad Agents**, a General profile that stays on the keypad whichever app is in front.
Its home page:

| | | |
| --- | --- | --- |
| Coding sessions | Scheduled | Usage |
| Chats | Projects | Workspaces |
| Next waiting | New | Switch profile |

## Requirements

- macOS 15 or later, with Logi Options+ and a Logitech MX Keypad.
  The Birchpad status line uses the `jq` that ships with macOS 15 at `/usr/bin/jq`.
- Ghostty 1.3 or later, for its AppleScript support, and herdr 0.9 or later.
- The `claude` and `codex` CLIs, for agent status, thread names, and usage.
- Python 3, for the hook installers and the profile copy in step 6.

## 1. Get the plugin packages

Pick one:

- Ask the maintainer for `BirchpadAgentHub.lplug4` and `BirchpadHerdr.lplug4`, plus `BirchpadClaude.lplug4` and `BirchpadCodex.lplug4` if wanted.
- Or build them once; this is one packaging command, not a development setup.
  Run it from the root of your clone of this repository:

```sh
brew install dotnet
export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec
export PATH=/opt/homebrew/opt/dotnet/bin:$PATH:$HOME/.dotnet/tools
dotnet tool install --global LogiPluginTool
tools/package.sh
```

`tools/package.sh` runs the tests and writes one `.lplug4` per plugin to `dist/`.
It builds the iTerm plugin too; leave that one uninstalled.

On a Mac whose Homebrew is managed by Workbrew (`/opt/homebrew` owned by a `workbrew` user), `brew install` needs an account with admin rights.
Do not run the `sudo chown` that plain Homebrew suggests there; it takes the install away from Workbrew.

All later commands also run from the repository root.

## 2. Remove overlapping plugins

Options+ may already have other plugins for the same agents and apps.
Each one you keep adds its own hooks or keys alongside Birchpad's.

- Claude Desktop (Beta), Logitech's plugin, adds hooks to `~/.claude/settings.json` and holds permission requests.
  To answer approvals from the Birchpad keys, remove it in Options+, then check for leftover hooks:

  ```sh
  python3 hooks/claude_hooks.py remove-legacy
  ```

  "removed 0" means uninstalling it already took its hooks out.
- Claude Console, a third-party plugin for Apple Terminal, leaves five hooks and its own status line in `~/.claude/settings.json` after it is removed in Options+.
  Its own cleanup script takes them out and puts back any status line it replaced; it also deletes `~/.claude/claude-console`, including that plugin's `prompts.json`, so copy that file first if you want it:

  ```sh
  bash ~/.claude/claude-console/scripts/uninstall.sh --dry-run   # preview
  bash ~/.claude/claude-console/scripts/uninstall.sh --yes
  ```

- Codex Desktop and other herdr plugins can stay, but they duplicate what Birchpad does; remove Codex Desktop if you install Birchpad Codex.

Uninstalling a plugin leaves its app entry and profile in Options+; delete those by hand if you want them gone.

## 3. Install the plugins

Install Agent Hub before the Claude and Codex plugins and before step 6, since their keys point at Agent Hub; Herdr can go in either order.
Double-click each file, or:

```sh
open dist/BirchpadAgentHub.lplug4
open dist/BirchpadHerdr.lplug4
open dist/BirchpadClaude.lplug4   # optional
open dist/BirchpadCodex.lplug4    # optional
```

Accept the Options+ prompt for each.
The Claude and Codex plugins bring their own home pages, so nothing needs importing for them.
Do not use `logiplugintool install`; it fails against Logi Plugin Service.

## 4. Install the hooks

The hooks send agent status and usage to Agent Hub on `127.0.0.1:8766`.
No build is needed for this step.

```sh
python3 hooks/claude_hooks.py install --statusline --blocking-permissions
python3 hooks/codex_hooks.py install
```

- Leave off `--blocking-permissions` if Logitech's Claude Desktop plugin is still installed, so the two plugins do not both hold permission requests.
- `--statusline` installs the Birchpad status line (`model · folder · 42% context`), which also supplies Claude's live 5-hour and weekly limits to the Usage key.
  It does not replace a status line you already have; to use Birchpad's, save and remove yours first, then run the install:

  ```sh
  cp ~/.claude/settings.json ~/.claude/settings.json.before-birchpad
  python3 -c "import json,os; p=os.path.expanduser('~/.claude/settings.json'); s=json.load(open(p)); s.pop('statusLine',None); json.dump(s,open(p,'w'),indent=2)"
  ```

- Then run `codex`, type `/hooks`, and trust the nine Birchpad entries; Codex skips them until they are trusted.
  An entry for herdr's own `herdr-agent-state.sh` may be listed too; it is not Birchpad's.
- Both installers back up the file they change, and `remove` takes the Birchpad entries out again.

## 5. Check macOS permissions

Logi Plugin Service needs permission to control the computer for the Claude Desktop and ChatGPT keys (pasting, menu clicks, Esc).
Options+ usually turns it on when it is installed; check that LogiPluginService is enabled in:

- macOS 27: System Settings > Privacy & Security > Device Control and Data Access.
- macOS 15: System Settings > Privacy & Security > Accessibility.

If it is missing, add `/Applications/Utilities/LogiPluginService.app` with the + button.
The top-level Accessibility pane holds accessibility features, not this list, and Full Disk Access is a different permission that Birchpad does not need.

Agents in herdr panes need no permission, since herdr is driven through its socket.
macOS asks once for Automation permission the first time a key reaches Ghostty, Claude Desktop, or the ChatGPT app; allow it.

## 6. Import the Agents profile

A profile is a key layout.
Options+ normally switches profiles to match the app in front (Adapt to App); the Agents profile is meant to stay on the keypad whatever app is in front.

### Make a copy for your Mac

The bottom-right Switch profile key in `profiles/generated/Birchpad Agents.lp5` points at the ID of the maintainer's "Default General Profile", and that ID differs on every Mac.
This writes a copy to `dist/Birchpad Agents.lp5` whose Switch profile key points at your own "Default General Profile":

```sh
python3 - <<'EOF'
import glob, json, os, zipfile
src = "profiles/generated/Birchpad Agents.lp5"
dst = "dist/Birchpad Agents.lp5"
apps = os.path.expanduser("~/Library/Application Support/Logi/LogiPluginService/Applications/Loupedeck70")
general = [json.load(open(f, encoding="utf-8-sig")) for f in sorted(glob.glob(f"{apps}/@_defaultmac/Profiles/*/ProfileInfo.json"))]
named = [p for p in general if p.get("displayName") == "Default General Profile"]
target = (named or general)[0]
mine = target["name"]
with zipfile.ZipFile(src) as zin:
    info = json.loads(zin.read("ProfileInfo.json").decode("utf-8-sig"))
    theirs = info["profileActions"][0]["actionParameters"]["parameters"]["profileName"]
    os.makedirs("dist", exist_ok=True)
    with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as zout:
        for item in zin.infolist():
            data = zin.read(item.filename)
            if item.filename == "ProfileInfo.json":
                data = data.decode("utf-8-sig").replace(theirs, mine).encode("utf-8")
            zout.writestr(item, data)
print(f"wrote {dst}; Switch profile points at {target.get('displayName')} ({mine})")
EOF
```

Run it from the repository root.
It points Switch profile at the keypad's "Default General Profile", which Options+ creates for the keypad.
If that profile was renamed, it uses the first General profile instead; the last line names the one it chose.

### Import it

1. Open Logi Options+ and click the MX Keypad.
   Button names below may differ slightly between Options+ versions.
2. Import the copy: double-click `dist/Birchpad Agents.lp5` in Finder (or `open "dist/Birchpad Agents.lp5"`), or use Import in the keypad's profile list.
3. Birchpad Agents appears among the General profiles, beside "Default General Profile"; select it.
4. Turn off Adapt to App, so the keypad stays on Birchpad Agents when you change apps.
5. Check the keys: Coding sessions, Scheduled, and Usage on the top row; Chats, Projects, and Workspaces in the middle; Next waiting, New, and Switch profile on the bottom.
   Switch profile goes to your Default General Profile.

Keys that show blank or missing mean a plugin is not loaded; check that Agent Hub and Herdr are installed (step 3).

The other files in `profiles/generated/` are for other setups.
Options+ refuses to import the per-app profiles (such as `Birchpad Claude.lp5`); the plugins install those themselves.

## 7. Check it works

```sh
curl http://127.0.0.1:8766/health     # prints "ok"
curl http://127.0.0.1:8766/sessions   # the sessions Agent Hub knows
```

Start `claude` in a herdr pane in Ghostty and send it a prompt.
A new Claude Code session appears after its first prompt, not when `claude` starts.
Sessions are never on the home page: press Coding sessions, then the session's tile, to bring its pane forward and open its agent page.
Next waiting jumps straight to the session that most needs you, including one that has just finished.

To check a plain Ghostty tab, run `claude` in a tab outside herdr, send a prompt, switch to another app, and press its tile: Ghostty should come forward on that tab.
Then send a slash command from its agent page; it should appear in the tab and run.

Plugin logs are in `~/Library/Application Support/Logi/LogiPluginService/Logs/plugin_logs/`.

## Using it

### Status rings

| Ring | Meaning |
| --- | --- |
| Blue | Working |
| Amber, pulsing | Needs approval |
| Purple, pulsing | Asking a question |
| Green | Done |
| Red | Failed |
| Gray | Idle |

### Navigation

- In every folder, Back is the top-left key and More is the bottom-right key when a list runs past one page.
- Next waiting (bottom left on the home page) jumps to the agent that most needs you.
- An agent's page shows its context use and the weekly limit, then your slash commands, sent to that agent.
- While the agent waits on a menu, its page shows that menu's options instead; pressing one answers and returns to the list.
- The Herdr folders (Agents, Workspaces, Worktrees, Panes, Agent keys) can be dragged onto any profile in Options+.

### Your own commands and prompts

These files live in `~/Library/Application Support/Birchpad/`, are created on first use, and are reread each time their folder opens.

- `slash-commands.json`: `[{"name": "...", "command": "...", "submit": true}]`, shared by every app and agent page.
  With `"submit": false` the command is typed without Return, for commands that take arguments; keep its trailing space.
- `prompts.json`: `[{"name": "...", "text": "..."}]`, saved prompts for the Claude and Codex pages.

## Things to know

- When the `claude` CLI is installed, Agent Hub runs it headless every 30 minutes to list cloud routines and sessions on other machines.
  Each run costs about $0.02 of subscription usage, roughly $1 a day; the `cloudIntervalMinutes` plugin setting lengthens the interval (minimum 5).
- The Agents profile is the newest part of the project; some of its behavior is still awaiting review on the keypad.
- Options+ never refreshes an installed profile from a newer default, and a renamed or removed action breaks its key in existing profiles.
  After an update, re-import the profile if keys stop working.

## Uninstall

```sh
python3 hooks/claude_hooks.py remove
python3 hooks/codex_hooks.py remove
```

`remove` also clears the Birchpad status line; restore your own from `~/.claude/settings.json.before-birchpad` if you saved one in step 4.
Then remove the plugins in Options+.
Uninstalling a plugin leaves its app entry and profile behind in Options+; delete those by hand.
