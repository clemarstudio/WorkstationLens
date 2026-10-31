# Workstation Lens

**A local workload history for Windows — useful when you want to ask an LLM what your PC actually needs.**

Task Manager is good at telling me what's happening *right now*. I wanted something that would remember what happened while I was working, gaming, running local AI models or leaving background jobs on overnight.

Workstation Lens quietly records resource usage over time. You can inspect the trends in a local dashboard, then export a time range and give the evidence to ChatGPT, Claude or another LLM of your choice. There is no account, API key or built-in cloud connection.

![Workstation Lens dashboard](docs/screenshots/dashboard.png)

*The single-page dashboard: current CPU/GPU/RAM/VRAM usage and interactive history charts.*

## What can I use it for?

A few examples of questions I wanted to answer:

- Is 16 or 24 GB of VRAM enough for the local AI workloads I actually run?
- Which applications keep consuming CPU, memory or I/O long after I finish using them?
- Would a workstation with less RAM still handle my usual work?
- Is a background job light enough to move to a smaller always-on machine?

The tool collects evidence, **not automatic hardware recommendations**. CPU utilisation on one computer cannot predict how fast a different CPU will finish a task. I still use real task timings and controlled comparisons before making hardware decisions.

## How it works

The collector runs in the Windows notification area, even when the dashboard is closed. There is no permanent console window.

- Samples the machine every **15 seconds**.
- Shows live CPU, GPU, RAM and VRAM, with interactive compute, memory, paging and storage-latency charts.
- Stores **one-minute machine history**, **15-minute application summaries** and short-term **per-process (PID) details** in local SQLite.
- Keeps system/application trends for up to **90 days** and individual PID records for **14 days**.
- Lets you choose a preset period or a custom date/time range. **Export** downloads a ZIP for that exact selection.

### Exporting for an LLM

The ZIP contains four files:

| File | What it's for |
|---|---|
| `report.md` | Readable summary, percentiles, active applications and measurement caveats |
| `evidence.json` | Structured evidence for deeper LLM or scripted analysis |
| `workstation-lens-records.csv` | Detailed records for Excel or your own analysis |
| `README.txt` | Short guide to the files, columns and units |

I usually start by attaching `report.md` and `evidence.json` to my LLM, with a specific question. For example, if I were considering downsizing a workstation:

> My current PC has 96 GiB RAM and 32 GiB GPU VRAM. Could a 32 GiB RAM / 24 GiB VRAM machine handle the same work? Use the measurements to explain the risks, but don't treat memory usage as a hard minimum requirement. Note missing observations and suggest what to benchmark before buying.

![Example analysis of a Workstation Lens export](docs/screenshots/llm-analysis-example.png)

*Illustrative LLM-style analysis using anonymized measurements from a real local export. The 49.4 GiB peak was measured on the original 96 GiB RAM PC, not the proposed 32 GiB machine. Recreated for documentation; not a screenshot of a third-party chat.*

The CSV contains machine, application and PID rows in one file; filter by `record_type`. Empty cells mean unavailable or not applicable, **not zero**.

## Try it

**Current status:** source preview. The signed Windows installer and tested signed-upgrade path are not published yet; please build from source for now.

Requires **Windows 11 x64** and the **.NET 10 SDK**. Download this repository (using **Code → Download ZIP** on GitHub), extract it, then open PowerShell in the extracted folder:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Start-Lens.ps1
```

The dashboard opens at [http://127.0.0.1:18765](http://127.0.0.1:18765). Once started, you can close the browser and leave the collector running. To stop it, use `Stop-Lens.cmd`. The optional `Enable-Autostart.ps1` script sets up a per-user Windows logon task; `Disable-Autostart.ps1` removes it.

## How lightweight is it?

I checked the collector on **31 October 2026, around 19:00 UTC**, on a Windows 11 workstation with 32 logical processors and roughly 500 running processes. These are short spot measurements, **not a 24-hour CPU or RAM benchmark**:

| Measurement | Observed | Measurement window |
|---|---|---|
| RAM working set | About **145–148 MiB** | Spot readings during approximately one minute |
| Private memory | About **70–73 MiB** | Same short observation period |
| CPU | About **0.01% of total CPU** | One **20-second** interval; occasional short activity |
| Collection time | Roughly **19–51 ms** | Several individual collection passes, scheduled every 15 seconds |
| Local history database | Around **108 MiB**, plus a **5 MiB** SQLite write-ahead log | Point-in-time size after about **1,990 recorded machine-minutes (33 hours)** accumulated since 29 October 2026 |

The database had accumulated measurements over roughly **55 hours of wall-clock time**, with gaps where no data was recorded; it was **not a 90-day database-size test**. These figures come from *one machine*, not guaranteed limits or a sustained-load benchmark. CPU, memory and database size depend on the system, process count and retention history.

The unsigned **self-contained installer test** was about **72 MiB**. Most of that is the bundled .NET / Windows runtime, not the application itself; a framework-dependent publish was about 2.8 MiB, but requires compatible runtimes already installed. No unsigned test installer is being offered as a public release.

## Privacy and limitations

Everything is recorded in `%LOCALAPPDATA%\WorkstationLens\measurements.db`. The dashboard listens on localhost and doesn't need a cloud account. **Nothing is automatically uploaded to an LLM.** You decide whether and where to share an export.

Exports can include application names and any workload notes you enter. Check the files before attaching them to an online service. The collector doesn't capture document names, command lines, browser URLs or window titles.

GPU percentages reflect Windows WDDM engines, not CUDA compute occupancy. Per-process GPU memory attribution is not the same as total physical VRAM. Short spikes between samples may be missed. These limitations are noted in the exported report so an LLM has a better chance of interpreting the evidence correctly.

## License

MIT — see [LICENSE](LICENSE). Built by Clemar Studio.
