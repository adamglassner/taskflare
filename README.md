# Taskflare

A free, open-source to-do list for Windows 10 and 11, by **Bytehawk Studio**. It mixes the best parts of
Microsoft To Do (My Day, Important, Planned, steps, a details pane) and Todoist (quick add that understands
dates, `#lists` and priorities), in Light or Dark mode. Add as many tasks and lists as you like. No account,
no subscription, nothing locked behind a paywall.

It also works with **AI assistants**: Claude, ChatGPT, Gemini, Grok and Meta's Muse can add and read your tasks, and Siri can add them through an Apple Shortcut ("add eggs and milk to my Shopping list in Taskflare").

A mobile version that syncs with this one is planned.

## Download

- **Bytehawk Studio Launcher**: install Taskflare from the *Bytehawk Apps* page; the launcher keeps it up to date.
- Or grab the latest release from [Releases](https://github.com/adamglassner/taskflare/releases):
  - `Taskflare-<version>-Setup.exe`: installer (choose *Install for me only* if you don't have admin rights)
  - `Taskflare-<version>-win-x64.zip`: portable, unzip and run `Taskflare.exe` (no .NET needed)

## Features

| | |
|---|---|
| **My Day** | Your list for today. Tasks due today (and overdue ones) show up automatically; the lightbulb suggests more. |
| **Important / Planned / Tasks** | Starred tasks; everything with a due date grouped into Earlier, Today, Tomorrow, This week and Later; and the inbox for tasks not in a list. |
| **Lists** | As many as you want, each with a colour and icon. Drag tasks onto a list to move them; drag lists to reorder. |
| **Quick add** | Type naturally and press Enter. Chips under the box show what Taskflare understood. |
| **Details** | Steps, My Day, reminder, due date and time, repeat, priority, list, labels and notes. |
| **Reminders** | Pop up in the corner of the screen with Complete and Snooze. Taskflare keeps running in the notification area when you close it (you can turn that off). |
| **AI assistants** | Claude, ChatGPT, Gemini, Grok, Muse and Siri can add, read and complete tasks (see below). Tasks they add get a badge with the assistant's name and appear in *Added by AI*. |
| **Sounds** | A little sound when you check off a task (Chime, Ding, Pop or Bubble), which you can turn off. |
| **Your data** | Saved in `%APPDATA%\Taskflare\tasks.json`, with a daily backup in `backups\`. Export and import from Settings. |

### Quick add cheat sheet

| Type | Means |
|---|---|
| `today`, `tomorrow`, `tonight`, `fri`, `next monday`, `next week`, `in 3 days`, `nov 20`, `12/1`, `2026-12-01` | Due date |
| `5pm`, `at 17:30`, `at 3`, `noon` | Due time (and a reminder at that time) |
| `every day`, `every weekday`, `every monday`, `weekly`, `monthly`, `yearly` | Repeat |
| `#Work`, `#Grocery list` | Put it in that list |
| `p1` / `p2` / `p3`, or `!!!` / `!!` | High / medium / low priority |
| `*` | Important (starred) |
| `@errands` | Label |

Example: `Call mom tomorrow at 5pm #Personal p1`. Turn this off in Settings if you'd rather type titles literally.

### Keyboard

`Ctrl+N` new task · `Ctrl+F` search · `Ctrl+D` complete the selected task · `Space` complete · `Delete` delete · `Enter` edit the title · `Esc` close the details pane

## AI assistants

Taskflare includes a small web server, off by default, that lets AI assistants work with your tasks. Set it up in **Settings › AI assistants**:

1. **Turn on the AI connector.** It listens on this PC only (`http://localhost:47811`).
2. **Start the public link.** Assistants run in the cloud, so they can't reach `localhost`. Taskflare downloads Cloudflare's free
   `cloudflared` and starts a *quick tunnel*, which gives you a private `https://….trycloudflare.com` address. No Cloudflare
   account needed. The address changes each time the tunnel starts.
3. **Connect your assistant.** Each assistant gets its own link (`https://…/mcp/<secret key>/<assistant>`), so Taskflare can show who added each task.

| Assistant | How | Plans |
|---|---|---|
| **Claude** | Settings › Connectors › *Add custom connector*, paste the link. Works in the Claude apps on your phone too. | All plans (Free allows one custom connector) |
| **ChatGPT** | Turn on *Developer mode* (Settings › Apps & Connectors › Advanced), create a connector with the link and *No authentication*. | Paid plans with Developer mode |
| **Gemini** | gemini.google.com › Settings › Connected Apps › *Add a custom app*, paste the link. | Google AI Ultra (Gemini Spark), personal accounts, English |
| **Grok** | grok.com/connectors › *New Connector* › *Custom*, paste the link. | Paid Grok plans |
| **Muse** | Settings › Connectors › *Add custom connector* if your Muse has it; otherwise send Muse the setup message from Taskflare. | — |
| **Siri** | An Apple Shortcut: *Dictate Text* → *Get Contents of URL* (POST, JSON body `{"text": Dictated Text}`) to the Siri link. Say "Hey Siri, add to Taskflare". | iPhone, iPad, Mac |
| **Alexa** | Not yet. Amazon only lets developers connect apps to Alexa+ through certified add-ons. | — |
| **Others** | Any app that takes an MCP / custom connector link (Perplexity, Le Chat, Copilot Studio, Cursor…), or the JSON API. | — |

Then ask away: *"Add 'book flights' to my Work list for Friday, high priority"*, *"What's on my to-do list today?"*, *"Tick off buy milk"*.
You choose whether assistants may complete, change and delete tasks, or only add and read them.

### For developers

**MCP** (streamable HTTP, JSON responses): `POST /mcp/<key>` or `POST /mcp/<key>/<assistant>` (claude, chatgpt, gemini, grok, muse…). Without the assistant part, Taskflare works it out from the client's name. Tools: `add_task`, `add_tasks`, `quick_add`, `list_tasks`,
`list_lists`, `complete_task`, `update_task`, `delete_task`, `create_list`.

**REST** (JSON): open `GET /api/<key>` for the full list. The main ones:

```
GET    /api/<key>/tasks?view=my_day|important|planned|overdue|today|completed|muse&list=Work&search=milk
POST   /api/<key>/tasks            {"title":"Book dentist","list":"Personal","due_date":"2026-11-20","due_time":"14:00","priority":"high"}
POST   /api/<key>/quick-add        {"text":"Call mom tomorrow 5pm #Personal p1"}
PATCH  /api/<key>/tasks/<id>       {"important":true}
POST   /api/<key>/tasks/<id>/complete
DELETE /api/<key>/tasks/<id>
GET    /api/<key>/lists
```

Add `?via=<assistant>` to REST calls to label the tasks they add. The key can also be sent as `Authorization: Bearer <key>` (then use `/mcp` and `/api/...` without it in the path).
Make a new key in Settings at any time; the old link stops working.

## Building

Requirements: Windows, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and, for the installer, Inno Setup 6.

```powershell
winget install Microsoft.DotNet.SDK.8
winget install JRSoftware.InnoSetup
.\build.ps1                 # dist\Taskflare.exe, dist\Taskflare-<ver>-win-x64.zip, dist\Taskflare-<ver>-Setup.exe
.\build.ps1 -SkipInstaller  # exe and zip only
```

Taskflare is C# / WPF on .NET 8, published as a self-contained single-file exe. `Taskflare.exe --self-test out.txt shots\`
runs the automated test (parser, store, the Muse connector over real HTTP, and screenshots of every view in Light and Dark).

Every push builds and tests on GitHub Actions. Pushing to `main` with a new `<Version>` in `src/Taskflare/Taskflare.csproj`
(and a matching section in `CHANGELOG.md`) publishes a GitHub release, which the Bytehawk Studio Launcher picks up.

## Licence

MIT. © 2026 Bytehawk Studio.
