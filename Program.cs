using System.Collections.Concurrent;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NAudio.Wave;


// The101BoxWeb: a simple ASP.NET Core app to control Yaesu FTDX101 radios via serial port, with a pixel-exact HTML/JS UI matching the desktop app.
// version 0.9  by Kees, ON9KVE (based on The101Box 3.01)
// date : 19 apr 2026


// ── parse command-line arguments ─────────────────────────────────────────────
var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
string comPort  = Arg(cmdArgs, "--port",  "");
int    httpPort = int.Parse(Arg(cmdArgs, "--http", "5000"));
int    baudRate = int.Parse(Arg(cmdArgs, "--baud", "38400"));

// ── shared objects ────────────────────────────────────────────────────────────
var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var state    = new RadioState();
var clients  = new ConcurrentDictionary<string, WebSocket>();
var engine   = new RadioEngine(state, jsonOpts, clients, baudRate);
var audio    = new AudioEngine();

// ── log ring buffer ───────────────────────────────────────────────────────────
var logLines = new List<string>();
var logLock  = new object();
void AppLog(string msg) {
    lock (logLock) { logLines.Add($"[{DateTime.Now:HH:mm:ss}] {msg}"); if (logLines.Count > 8) logLines.RemoveAt(0); }
}
engine.Log = AppLog;
audio.Log  = AppLog;

// ── ASP.NET Core minimal API ──────────────────────────────────────────────────
var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls($"http://*:{httpPort}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.MapGet("/", () =>
{
    var devFile = Path.Combine(AppContext.BaseDirectory, "index.html");
    var html    = File.Exists(devFile) ? File.ReadAllText(devFile) : Resources.HtmlPage;
    return Results.Content(html, "text/html");
});

app.MapGet("/api/ports", () =>
    Results.Json(SerialPort.GetPortNames().OrderBy(p =>
        int.TryParse(p.Replace("COM", "").Replace("com", ""), out int n) ? n : 999)));

app.MapGet("/api/audiodevices", () =>
    Results.Json(AudioEngine.GetDevices().Select(d => new { d.index, d.name })));

app.Map("/audio/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    var id = Guid.NewGuid().ToString();
    audio.AddClient(id, ws);
    try
    {
        // keep the socket alive until the client disconnects
        var buf = new byte[256];
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buf, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) break;
            // handle start/stop commands from browser
            var cmd = Encoding.UTF8.GetString(buf, 0, result.Count);
            if (cmd.StartsWith("START:") && int.TryParse(cmd[6..], out int devIdx))
                audio.Start(devIdx);
            else if (cmd == "STOP")
                audio.Stop();
        }
    }
    finally
    {
        audio.RemoveClient(id);
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
    }
});

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    var id = Guid.NewGuid().ToString();
    clients[id] = ws;
    try
    {
        await engine.SendStateAsync(ws);
        await engine.ReceiveLoopAsync(ws, engine.Stopping);
    }
    finally
    {
        clients.TryRemove(id, out _);
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
    }
});

// connect to radio if port was given on command line
if (!string.IsNullOrEmpty(comPort))
    engine.Connect(comPort);

// start poll loop
var cts = new CancellationTokenSource();
engine.Stopping = cts.Token;
_ = engine.PollLoopAsync(cts.Token);
app.Lifetime.ApplicationStopping.Register(() => { cts.Cancel(); audio.Stop(); });

string _localIp = GetLocalIp();
try { Console.CursorVisible = false; Console.Clear(); } catch { }
_ = Task.Run(async () => {
    while (!cts.Token.IsCancellationRequested)
    {
        try { DrawStatus(state, clients, httpPort, _localIp, baudRate, logLines, logLock); } catch { }
        try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; }
    }
});

AppLog($"Server started on http://localhost:{httpPort}");
if (!string.IsNullOrEmpty(comPort))
    AppLog($"Connecting to {comPort} @ {baudRate} baud...");
else
    AppLog("No COM port specified — select one in browser.");

try
{
    await app.RunAsync();
}
catch (Exception ex)
{
    try { Console.CursorVisible = true; Console.Clear(); } catch { }
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("=== The101BoxWeb failed to start ===");
    Console.ResetColor();
    Console.WriteLine(ex.Message);
    Console.WriteLine();
    Console.WriteLine("Common causes:");
    Console.WriteLine($"  - Port {httpPort} is already in use by another application.");
    Console.WriteLine("  - Insufficient permissions to bind to the network.");
    Console.WriteLine();
    Console.WriteLine($"Try running with a different port:  The101BoxWeb.exe --http 5001");
    Console.WriteLine();
    Console.WriteLine("Press any key to exit...");
    try { Console.ReadKey(true); } catch { }
    return;
}
try { Console.CursorVisible = true; } catch { }

// ── helpers ───────────────────────────────────────────────────────────────────
static string Arg(string[] a, string name, string def)
{
    int i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
}

static string GetLocalIp()
{
    try
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
        s.Connect("8.8.8.8", 65530);
        return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
    }
    catch { return ""; }
}

static void DrawStatus(RadioState st, ConcurrentDictionary<string, WebSocket> cls,
    int port, string localIp, int baud, List<string> log, object lk)
{
    const int W = 62;
    var sb = new StringBuilder();
    void L(string s = "")
    {
        if (s.Length < W) s += new string(' ', W - s.Length);
        else if (s.Length > W) s = s[..W];
        sb.AppendLine(s);
    }
    string hr  = new string('═', W);
    string hr2 = new string('─', W);

    L(hr); L("  The101BoxWeb  ─  Yaesu FTDX101 Remote Control"); L(hr);
    L($"  URL     :  http://localhost:{port}");
    L(string.IsNullOrEmpty(localIp) ? "" : $"  Network :  http://{localIp}:{port}");
    L();
    if (st.Connected)
    {
        L($"  Radio   :  {st.RadioModel}  @  {st.ComPort}  ({baud} baud)");
        L($"  Main VFO:  {StFmt(st.MainFreqHz)}  {StMode(st.MainMode)}{(st.MainFocused ? "  ◄" : "")}");
        L($"  Sub VFO :  {StFmt(st.SubFreqHz)}  {StMode(st.SubMode)}{(!st.MainFocused ? "  ◄" : "")}");
        L($"  Width   :  {st.WidthPos:D2}   Shift: {(st.ShiftSteps * 20):+0;-0;0} Hz");
    }
    else
    {
        L("  Radio   :  NOT CONNECTED  (select port in browser)");
        L(); L(); L();
    }
    L($"  Browser :  {cls.Count} client(s) connected");
    L(); L(hr2);
    string[] lines;
    lock (lk) lines = log.ToArray();
    foreach (var ln in lines) L($"  {ln}");
    for (int i = lines.Length; i < 8; i++) L();
    L(hr); L($"  {DateTime.Now:HH:mm:ss}  │  Press Ctrl+C to stop"); L(hr);

    Console.SetCursorPosition(0, 0);
    Console.Write(sb.ToString());
}

static string StFmt(long hz)
    => $"{hz / 1_000_000,3}.{(hz % 1_000_000) / 1000:000}.{hz % 1000:000}";

static string StMode(string m) => m switch
    { "1"=>"LSB","2"=>"USB","3"=>"CW","4"=>"FM","5"=>"AM","C"=>"DIG",_=>"---" };

static int StVal(int v) => (int)Math.Round(v / 255.0 * 100);

// ── embedded HTML page ────────────────────────────────────────────────────────
static class Resources
{
public const string HtmlPage = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>The101BoxWeb</title>
<style>
* { box-sizing:border-box; margin:0; padding:0; }
body { background:#111; color:#ccc; font-family:Verdana,sans-serif; font-size:12px; user-select:none; }

#radio-info { color:#888; font-size:11px; }
#st-refresh { background:#333; color:#ccc; border:1px solid #555; padding:2px 6px; cursor:pointer; font-size:13px; margin-left:6px; }
#st-refresh:hover { background:#555; }

#status-bar { display:flex; align-items:center; gap:8px; padding:3px 10px; background:#0d0d0d; border-bottom:1px solid #222; font-size:11px; }
.st-dot { font-size:14px; line-height:1; }
.st-ok  { color:#00dd00; }
.st-bad { color:#ff4444; }
.st-warn{ color:#ffaa00; }
#st-radio-lbl { flex:1; }
#st-disc { margin-left:auto; background:#333; color:#ccc; border:1px solid #555; padding:2px 8px; cursor:pointer; font-size:11px; }
#st-disc:hover { background:#555; }

/* ── Main canvas (pixel-exact layout from Form1.Designer.cs, 727×241) ── */
#canvas-wrap { padding:4px; overflow-x:auto; background:#111; }
#canvas { position:relative; width:551px; height:244px; background:#0d0d0d; }

/* Buttons — DarkGreen / Yellow / White border, matching desktop exactly */
.btn {
  position:absolute;
  background:#006400; color:#ff0; border:2px solid #fff;
  font-family:Verdana,sans-serif; font-size:8pt; font-weight:bold;
  cursor:pointer; text-align:center; line-height:1.3; padding:0;
}
.btn:hover  { background:#006400; }
.btn:active { background:#cc0000; }
.btn.active { background:#8b0000 !important; }
.btn.active:hover { background:#aa0000 !important; }

#btn-rx1
#btn-rx1.active { background:#8b0000; color:#ff0; }
#btn-rx2 { background:#00008b; color:silver; }
#btn-rx2.active { background:#8b0000; color:#ff0; }

/* Frequency boxes */
.freq-box {
  position:absolute; background:#000; color:gold;
  font-family:'Courier New',monospace; font-size:26px; font-weight:bold;
  border:2px solid #555; text-align:center; letter-spacing:1px;
  cursor:pointer; display:flex; align-items:center; justify-content:center;
}

/* Labels / value displays */
#temp-val { position:absolute; font-size:10px; font-weight:bold; color:cyan; background:#000; text-align:center; }
#lev-val  { position:absolute; font-family:monospace; font-size:13px; color:limegreen; background:#000; text-align:center; border:1px solid #444; display:flex; flex-direction:column; align-items:center; justify-content:center; line-height:1.4; }
.sl-lbl   { position:absolute; font-size:7pt; color:#fff; text-align:center; }
.sl-val   { position:absolute; font-family:monospace; font-size:14px; color:gold; background:#000; text-align:center; border:1px solid #333; display:flex; align-items:center; justify-content:center; }

/* Sliders */
input[type=range].vslider {
  position:absolute; writing-mode:vertical-lr; direction:rtl; cursor:pointer;
  -webkit-appearance:none; appearance:none;
  background-color:silver;
  background-image:repeating-linear-gradient(to bottom,rgba(0,0,0,.18) 0,rgba(0,0,0,.18) 1px,transparent 1px,transparent 10px);
  border:1px solid #aaa; border-radius:2px;
}
/* MAIN sliders: silver body, thin grey groove, dark blue knob */
input[type=range].vslider::-webkit-slider-runnable-track { background:#888; width:3px; border-radius:2px; }
input[type=range].vslider::-moz-range-track             { background:#888; width:3px; border-radius:2px; }
input[type=range].vslider::-webkit-slider-thumb {
  -webkit-appearance:none;
  width:26px; height:10px; border-radius:2px;
  border:1px solid #000044; cursor:pointer; background:#00008b;
}
input[type=range].vslider::-moz-range-thumb {
  width:26px; height:10px; border-radius:2px;
  border:1px solid #000044; cursor:pointer; background:#00008b;
}

/* Step select inside canvas */
.cv-sel { position:absolute; background:#006400; color:#ff0; border:1px solid #fff; font-family:Verdana,sans-serif; font-size:7pt; font-weight:bold; }

/* Audio bar */
#audio-bar { display:flex; align-items:center; gap:8px; padding:4px 10px; background:#0d0d0d; border-top:1px solid #222; font-size:11px; }
#audio-bar select { background:#222; color:#ccc; border:1px solid #555; font-size:11px; padding:1px 4px; max-width:260px; }
#btn-audio-refresh { background:#333; color:#ccc; border:1px solid #555; padding:2px 6px; cursor:pointer; font-size:13px; }
#btn-audio-refresh:hover { background:#555; }
#btn-audio { background:#006400; color:#ff0; border:1px solid #fff; padding:2px 10px; cursor:pointer; font-size:11px; font-weight:bold; }
#btn-audio:hover { background:#008800; }
</style>
</head>
<body>

<!-- Status bar (always visible) -->
<div id="status-bar">
  <span class="st-dot st-warn" id="st-ws-dot">&#9679;</span>
  <span id="st-ws-lbl">Server: connecting...</span>
  <span style="color:#444; margin:0 6px;">&#x2502;</span>
  <span class="st-dot st-bad" id="st-radio-dot">&#9679;</span>
  <span id="st-radio-lbl">Radio: not connected</span>
  <span id="radio-info"></span>
  <button id="st-refresh" onclick="loadPorts()" title="Refresh COM ports">&#8635;</button>
</div>

<!-- Canvas: desktop layout matching Design101 (551×241) -->
<div id="canvas-wrap"><div id="canvas">
  <div id="freq-m" class="freq-box" style="left:1px;top:2px;width:189px;height:54px;" onclick="sendCmd('VS0;')" title="Click to focus Main VFO">&nbsp;</div>
  <div id="freq-s" class="freq-box" style="left:1px;top:62px;width:189px;height:46px;" onclick="sendCmd('VS1;')" title="Click to focus Sub VFO">&nbsp;</div>
  <div id="temp-val" style="left:29px;top:142px;width:44px;height:20px;">--&#176;C</div>

  <!-- Left: step, port, connect -->
  <select id="step" class="cv-sel" style="left:6px;top:111px;width:86px;height:22px;">
    <option value="100">100 Hz</option>
    <option value="500">500 Hz</option>
    <option value="1000" selected>1 kHz</option>
    <option value="5000">5 kHz</option>
    <option value="9000">9 kHz</option>
    <option value="20000">20 kHz</option>
    <option value="50000">50 kHz</option>
  </select>
  <select id="port-sel" class="cv-sel" style="left:6px;top:173px;width:85px;height:22px;"><option>Loading...</option></select>
  <button class="btn" id="btn-connect" style="left:7px;top:201px;width:85px;height:22px;" onclick="toggleConnect()">Connect</button>

  <!-- Width / Shift sliders (x=98 / x=145, matching Design101) -->
  <div class="sl-lbl" style="left:98px;top:111px;width:45px;">WIDTH</div>
  <input type="range" class="vslider" id="sl-width" min="1" max="23" value="20" style="left:106px;top:123px;width:28px;height:100px;" oninput="sliderChange('sl-width',this.value)">
  <div class="sl-val" id="sl-width-val" style="left:98px;top:226px;width:45px;height:16px;cursor:pointer;" title="Double-click to reset" ondblclick="resetWidth()">---</div>

  <div class="sl-lbl" style="left:145px;top:111px;width:45px;">SHIFT</div>
  <input type="range" class="vslider" id="sl-shift" min="-60" max="60" value="0" style="left:153px;top:123px;width:28px;height:100px;" oninput="sliderChange('sl-shift',this.value)">
  <div class="sl-val" id="sl-shift-val" style="left:145px;top:226px;width:45px;height:16px;cursor:pointer;" title="Double-click to reset" ondblclick="resetShift()">0</div>

  <!-- Col1: VFO / mode -->
  <button class="btn" style="left:195px;top:1px;width:88px;height:40px;" onclick="bandStep(1)" oncontextmenu="bandStep(-1);return false;" title="Click=Band up  Right-click=Band down"><span id="band" style="font-size:13px;">BAND</span></button>
  <button class="btn" style="left:195px;top:41px;width:44px;height:40px;" onclick="freqStep(-1)">[-]</button>
  <button class="btn" style="left:239px;top:41px;width:44px;height:40px;" onclick="freqStep(1)">[+]</button>
  <button class="btn" style="left:195px;top:81px;width:88px;height:40px;" onclick="sendCmd('SV;')">&lt;===&gt;</button>
  <button class="btn" id="btn-usb" style="left:195px;top:121px;width:44px;height:40px;" onclick="sendCmd(modeCmd('2'))">USB</button>
  <button class="btn" id="btn-lsb" style="left:239px;top:121px;width:44px;height:40px;" onclick="sendCmd(modeCmd('1'))">LSB</button>
  <button class="btn" id="btn-am"  style="left:195px;top:161px;width:44px;height:40px;" onclick="sendCmd(modeCmd('5'))">AM</button>
  <button class="btn" id="btn-fm"  style="left:239px;top:161px;width:44px;height:40px;" onclick="sendCmd(modeCmd('4'))">FM</button>
  <button class="btn" id="btn-cw"  style="left:195px;top:201px;width:44px;height:40px;" onclick="sendCmd(modeCmd('3'))">CW</button>
  <button class="btn" id="btn-dig" style="left:239px;top:201px;width:44px;height:40px;" onclick="sendCmd(modeCmd('C'))">DIG</button>

  <!-- Col2: ANT / RX -->
  <button class="btn" id="btn-ant1"  style="left:284px;top:1px;width:88px;height:40px;" onclick="sendCmd(antCmd(1))">ANT1</button>
  <button class="btn" id="btn-ant2"  style="left:284px;top:41px;width:88px;height:40px;" onclick="sendCmd(antCmd(2))">ANT2</button>
  <button class="btn" id="btn-ant3"  style="left:284px;top:81px;width:88px;height:40px;" onclick="sendCmd(antCmd(3))">ANT3/RX</button>
  <button class="btn" id="btn-rfsql" style="left:284px;top:122px;width:88px;height:40px;" onclick="rfSqlCmd()">RF/SQL<br>MAIN</button>
  <button class="btn" id="btn-rx1"   style="left:284px;top:161px;width:88px;height:40px;" onclick="rxCmd('rx1')" title="Toggle MAIN RX">MAIN<br>RX</button>
  <button class="btn" id="btn-rx2"   style="left:284px;top:201px;width:87px;height:40px;" onclick="rxCmd('rx2')" title="Toggle SUB RX">SUB<br>RX</button>

  <!-- Col3: IPO / AMP — VFO knob fills empty rows 4-6 -->
  <button class="btn" id="btn-ipo"  style="left:373px;top:1px;width:88px;height:40px;" onclick="sendCmd(ipoCmd(0))">IPO</button>
  <button class="btn" id="btn-amp1" style="left:373px;top:41px;width:88px;height:40px;" onclick="sendCmd(ipoCmd(1))">AMP1</button>
  <button class="btn" id="btn-amp2" style="left:373px;top:81px;width:88px;height:40px;" onclick="sendCmd(ipoCmd(2))">AMP2</button>
  <canvas id="vfo-knob" width="84" height="84" style="position:absolute;left:420px;top:130px;cursor:grab;" title="VFO Tuning — drag or scroll"></canvas>
  <div style="position:absolute;left:418px;top:218px;width:88px;text-align:center;font-size:8pt;color:#ff0;font-weight:bold;">VFO TUNE</div>
  <div style="position:absolute;left:418px;top:231px;width:88px;text-align:center;font-size:7pt;color:#888;">uses step</div>

  <!-- Col4: ATT / NR / BC — matching Design101 x=462/505 -->
  <button class="btn" id="btn-att0"  style="left:462px;top:1px;width:44px;height:40px;" onclick="sendCmd(attCmd(0))">ATT<br>OFF</button>
  <button class="btn" id="btn-att12" style="left:462px;top:41px;width:44px;height:40px;" onclick="sendCmd(attCmd(2))">-12<br>dB</button>
  <button class="btn" id="btn-nr"    style="left:462px;top:81px;width:44px;height:40px;" onclick="nrCmd()">NR</button>
  <button class="btn" id="btn-att6"  style="left:505px;top:1px;width:44px;height:40px;" onclick="sendCmd(attCmd(1))">-6<br>dB</button>
  <button class="btn" id="btn-att18" style="left:505px;top:41px;width:44px;height:40px;" onclick="sendCmd(attCmd(3))">-18<br>dB</button>
  <button class="btn" id="btn-dnf"   style="left:505px;top:81px;width:44px;height:40px;" onclick="dnfCmd()">BC</button>
</div></div>

<!-- Audio bar -->
<div id="audio-bar">
  <span class="st-dot st-warn" id="audio-dot">&#9679;</span>
  <span id="audio-lbl">Audio: idle</span>
  <span style="color:#444; margin:0 8px;">&#x2502;</span>
  <label style="color:#aaa;font-size:11px;">Input device:</label>
  <select id="audio-dev-sel"></select>
  <button id="btn-audio-refresh" onclick="loadAudioDevices()" title="Refresh devices">&#8635;</button>
  <button id="btn-audio" onclick="toggleAudio()">&#9654; Start Audio</button>
</div>

<script>
let ws = null;
let state = { connected:false, mainFocused:true, rx1Active:false, rx2Active:false,
  nrOn:false, dnfOn:false, rfSqlOn:false,
  mainFreqHz:0, subFreqHz:0, mainMode:'2', subMode:'2',
  mainAnt:1, subAnt:1, mainIpo:0, subIpo:0, mainAtt:0, subAtt:0,
  widthPos:20, shiftSteps:0,
  temp:'--\u00b0C', tempColor:'cyan', radioModel:'FTDX101D' };
const timers = {};

// ── WebSocket ─────────────────────────────────────────────────────────────────
function connect() {
  setWsSt('warn', 'Server: connecting...');
  ws = new WebSocket(`ws://${location.host}/ws`);
  ws.onmessage = e => { const m = JSON.parse(e.data); if (m.type==='state') applyState(m.data); };
  ws.onopen    = () => setWsSt('ok',  'Server: connected');
  ws.onclose   = () => { setWsSt('bad', 'Server: disconnected \u2014 reconnecting...'); setTimeout(connect, 2000); };
  ws.onerror   = () => ws.close();
}
function setWsSt(cls, txt) {
  document.getElementById('st-ws-dot').className   = `st-dot st-${cls}`;
  document.getElementById('st-ws-lbl').textContent = txt;
}
function sendCmd(cmd) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({cmd}));
}

// ── State → UI ────────────────────────────────────────────────────────────────
function applyState(s) {
  state = s;
  document.getElementById('st-radio-dot').className   = `st-dot ${s.connected ? 'st-ok' : 'st-bad'}`;
  document.getElementById('st-radio-lbl').textContent = s.connected ? `Radio: ${s.radioModel}  \u2014  ${s.comPort}` : 'Radio: not connected';
  const cb = document.getElementById('btn-connect'); if (cb) { cb.textContent = s.connected ? 'Disconnect' : 'Connect'; cb.classList.toggle('active', s.connected); }

  updateFreqs(s);
  const bn = bandName(s.mainFocused ? s.mainFreqHz : s.subFreqHz);
  document.getElementById('band').textContent = bn === '---' ? 'BAND' : bn;

  const tv = document.getElementById('temp-val');
  tv.textContent = s.temp; tv.style.color = s.tempColor;

  setA('btn-usb',  s.mainMode==='2');
  setA('btn-lsb',  s.mainMode==='1');
  setA('btn-cw',   s.mainMode==='3');
  setA('btn-am',   s.mainMode==='5');
  setA('btn-fm',   s.mainMode==='4');
  setA('btn-dig',  s.mainMode==='C');

  setA('btn-ant1', s.mainAnt===1); setA('btn-ant2', s.mainAnt===2); setA('btn-ant3', s.mainAnt===3);
  setA('btn-ipo',  s.mainIpo===0); setA('btn-amp1', s.mainIpo===1); setA('btn-amp2', s.mainIpo===2);
  setA('btn-att0', s.mainAtt===0); setA('btn-att6',  s.mainAtt===1);
  setA('btn-att12',s.mainAtt===2); setA('btn-att18', s.mainAtt===3);

  setA('btn-rx1',   s.rx1Active); setA('btn-rx2', s.rx2Active);
  setA('btn-nr',    s.nrOn);      setA('btn-dnf', s.dnfOn);
  setA('btn-rfsql', s.rfSqlOn);
  document.getElementById('btn-rfsql').innerHTML = s.rfSqlOn ? 'SQUELCH<br>MAIN' : 'RF/SQL<br>MAIN';

  const mode = s.mainFocused ? s.mainMode : s.subMode;
  setSl('sl-width', s.widthPos,   shBwDisplay(s.widthPos, mode));
  setSl('sl-shift', s.shiftSteps, isShiftDisplay(s.shiftSteps));
  document.getElementById('sl-shift-val').style.color = s.shiftSteps !== 0 ? 'red' : 'gold';
}

function updateFreqs(s) {
  const m = document.getElementById('freq-m'), sub = document.getElementById('freq-s');
  m.textContent = fmtFreq(s.mainFreqHz); sub.textContent = fmtFreq(s.subFreqHz);
  if (s.mainFocused) {
    m.style.background='silver'; m.style.color='black';
    sub.style.background='black'; sub.style.color='gold';
  } else {
    m.style.background='black'; m.style.color='gold';
    sub.style.background='#00008b'; sub.style.color='white';
  }
}

// ── Helpers ───────────────────────────────────────────────────────────────────
function setA(id, on)  { document.getElementById(id)?.classList.toggle('active', !!on); }
function setSl(id, v, d) {
  const sl = document.getElementById(id), dv = document.getElementById(id+'-val');
  if (sl && parseInt(sl.value) !== v) sl.value = v;
  if (dv) dv.textContent = d;
}
function fmtFreq(hz) {
  const mhz=Math.floor(hz/1000000), khz=Math.floor((hz%1000000)/1000), rest=hz%1000;
  return `${mhz.toString().padStart(2,' ')}.${khz.toString().padStart(3,'0')}.${rest.toString().padStart(3,'0')}`;
}
function bandName(hz) {
  if(hz>=1810000&&hz<1900000)  return '160m'; if(hz>=3500000&&hz<3800000)  return '80m';
  if(hz>=5350000&&hz<5367000)  return '60m';  if(hz>=7000000&&hz<7200000)  return '40m';
  if(hz>=10100000&&hz<10150000)return '30m';  if(hz>=14000000&&hz<14350000)return '20m';
  if(hz>=18068000&&hz<18168000)return '17m';  if(hz>=21000000&&hz<21450000)return '15m';
  if(hz>=24890000&&hz<24990000)return '12m';  if(hz>=28000000&&hz<29700000)return '10m';
  if(hz>=50000000&&hz<54000000)return '6m';   return '---';
}
const shTable = [
  [0,0],[300,50],[400,100],[600,150],[850,200],[1100,250],[1200,300],[1500,350],
  [1650,400],[1800,450],[1950,500],[2100,600],[2200,800],[2300,1200],[2400,1400],[2500,1700],
  [2600,2000],[2700,2400],[2800,3000],[2900,3200],[3000,3500],[3200,4000],[3500,3500],[4000,4000]];
function shBwDisplay(pos, mode) {
  if (mode==='5') return '9000';
  if (mode==='4') return '16000';
  if (pos<1 || pos>=shTable.length) return '---';
  const cwDig = mode==='3' || mode==='C';
  return String(cwDig ? shTable[pos][1] : shTable[pos][0]);
}
function isShiftDisplay(steps) {
  const hz=steps*20;
  return hz===0 ? '0' : hz>0 ? `+${hz}` : `${hz}`;
}

// ── CAT command builders ──────────────────────────────────────────────────
const vfo    = ()  => state.mainFocused ? '0' : '1';
const modeCmd= m   => `MD${vfo()}${m};`;
const antCmd = n   => `AN${vfo()}${n};`;
const ipoCmd = n   => `PA${vfo()}${n};`;
const attCmd = n   => `RA${vfo()}${n};`;

function rxCmd(which) {
  const r1=which==='rx1'?!state.rx1Active:state.rx1Active;
  const r2=which==='rx2'?!state.rx2Active:state.rx2Active;
  sendCmd((r1&&r2)?'FR00;':(r1&&!r2)?'FR01;':(!r1&&r2)?'FR10;':'FR11;');
}
function nrCmd()    { sendCmd(`NR${vfo()}${state.nrOn ?'0':'1'};`); }
function dnfCmd()   { sendCmd(`BC${vfo()}${state.dnfOn?'0':'1'};`); }
function rfSqlCmd() { sendCmd(state.rfSqlOn?'EX0301070;':'EX0301071;'); }

function freqStep(dir) {
  const step=parseInt(document.getElementById('step').value);
  const freq=state.mainFocused?state.mainFreqHz:state.subFreqHz;
  const nf=Math.max(0,freq+dir*step);
  sendCmd(`${state.mainFocused?'FA':'FB'}${nf.toString().padStart(9,'0')};`);
}
function bandStep(dir) { sendCmd(dir>0?`BU${vfo()};`:`BD${vfo()};`); }
function resetWidth() {
  const mode = state.mainFocused ? state.mainMode : state.subMode;
  const pos  = (mode==='3' || mode==='C') ? 11 : 20;
  document.getElementById('sl-width').value = pos;
  sliderChange('sl-width', pos);
}
function resetShift() {
  document.getElementById('sl-shift').value = 0;
  sliderChange('sl-shift', 0);
}

// Sliders with 150 ms debounce
function sliderChange(id, raw) {
  const v   = parseInt(raw);
  const x   = state.mainFocused ? 0 : 1;
  const mode= state.mainFocused ? state.mainMode : state.subMode;
  const dv  = document.getElementById(id+'-val');
  if (id === 'sl-width') {
    if (dv) dv.textContent = shBwDisplay(v, mode);
  } else if (id === 'sl-shift') {
    if (dv) dv.textContent = isShiftDisplay(v);
    if (dv) dv.style.color = v !== 0 ? 'red' : 'gold';
  }
  clearTimeout(timers[id]);
  timers[id] = setTimeout(() => {
    let cmd;
    if (id === 'sl-width') {
      cmd = `SH${x}0${v.toString().padStart(2,'0')};`;
    } else if (id === 'sl-shift') {
      const hz   = v * 20;
      const sign = hz >= 0 ? '+' : '-';
      cmd = `IS${x}0${sign}${Math.abs(hz).toString().padStart(4,'0')};`;
    }
    if (cmd) sendCmd(cmd);
  }, 150);
}

// COM port setup
async function loadPorts() {
  try {
    const r=await fetch('/api/ports'); const ports=await r.json();
    const sel=document.getElementById('port-sel');
    sel.innerHTML=ports.length ? ports.map(p=>`<option>${p}</option>`).join('') : '<option>No ports found</option>';
  } catch(e) { console.error(e); }
}
function connectToPort()  { const p=document.getElementById('port-sel').value; if(p&&!p.startsWith('No '))sendCmd('CONNECT:'+p); }
function disconnectPort() { sendCmd('DISCONNECT'); }
function toggleConnect()  { if (state.connected) disconnectPort(); else connectToPort(); }

// ── Audio ─────────────────────────────────────────────────────────────────────
let audioWs       = null;
let audioCtx      = null;
let audioRunning  = false;
let nextPlayTime  = 0;
const SAMPLE_RATE = 16000;

async function loadAudioDevices() {
  try {
    const r = await fetch('/api/audiodevices');
    const devs = await r.json();
    const sel = document.getElementById('audio-dev-sel');
    sel.innerHTML = devs.length
      ? devs.map(d => `<option value="${d.index}">${d.name}</option>`).join('')
      : '<option>No devices found</option>';
  } catch(e) { console.error(e); }
}

function connectAudioWs() {
  audioWs = new WebSocket(`ws://${location.host}/audio/ws`);
  audioWs.binaryType = 'arraybuffer';
  audioWs.onmessage  = e => { if (audioRunning) scheduleAudio(e.data); };
  audioWs.onclose    = () => { if (audioRunning) setTimeout(connectAudioWs, 2000); };
  audioWs.onerror    = () => audioWs.close();
}

function scheduleAudio(arrayBuffer) {
  if (!audioCtx) return;
  const int16 = new Int16Array(arrayBuffer);
  const float32 = new Float32Array(int16.length);
  for (let i = 0; i < int16.length; i++) float32[i] = int16[i] / 32768;

  const buf = audioCtx.createBuffer(1, float32.length, SAMPLE_RATE);
  buf.copyToChannel(float32, 0);
  const src = audioCtx.createBufferSource();
  src.buffer = buf;
  src.connect(audioCtx.destination);

  const now = audioCtx.currentTime;
  if (nextPlayTime < now) nextPlayTime = now + 0.05; // small initial buffer
  src.start(nextPlayTime);
  nextPlayTime += buf.duration;
}

function toggleAudio() {
  if (!audioRunning) {
    audioCtx     = new AudioContext({ sampleRate: SAMPLE_RATE });
    nextPlayTime = 0;
    audioRunning = true;
    const devIdx = document.getElementById('audio-dev-sel').value;
    setAudioBtn(true);
    if (!audioWs || audioWs.readyState !== WebSocket.OPEN) {
      connectAudioWs();
      audioWs.onopen = () => audioWs.send('START:' + devIdx);
    } else {
      audioWs.send('START:' + devIdx);
    }
  } else {
    audioRunning = false;
    setAudioBtn(false);
    if (audioWs && audioWs.readyState === WebSocket.OPEN) audioWs.send('STOP');
    if (audioCtx) { audioCtx.close(); audioCtx = null; }
  }
}

function setAudioBtn(on) {
  const btn = document.getElementById('btn-audio');
  btn.textContent  = on ? '\u23F9 Stop Audio' : '\u25B6 Start Audio';
  btn.style.background = on ? '#cc0000' : '';
  document.getElementById('audio-dot').className  = `st-dot ${on ? 'st-ok' : 'st-warn'}`;
  document.getElementById('audio-lbl').textContent = on ? 'Audio: streaming' : 'Audio: idle';
}

// ── VFO Knob ──────────────────────────────────────────────────────────────────
function initKnob() {
  const el  = document.getElementById('vfo-knob');
  const ctx = el.getContext('2d');
  const CX  = el.width / 2, CY = el.height / 2, R = CX - 5;
  const DEG_PER_STEP = 10;
  let angle    = 0;
  let dragging = false;
  let lastAng  = 0;
  let accumDeg = 0;

  function draw() {
    ctx.clearRect(0, 0, el.width, el.height);
    // outer ring
    ctx.beginPath(); ctx.arc(CX, CY, R, 0, Math.PI*2);
    ctx.fillStyle = '#111'; ctx.fill();
    ctx.strokeStyle = '#555'; ctx.lineWidth = 2; ctx.stroke();
    // tick marks around ring
    for (let i = 0; i < 36; i++) {
      const a  = i * 10 * Math.PI / 180;
      const r1 = (i % 9 === 0) ? R - 7 : R - 4;
      ctx.beginPath();
      ctx.moveTo(CX + Math.sin(a)*r1,     CY - Math.cos(a)*r1);
      ctx.lineTo(CX + Math.sin(a)*(R-1),  CY - Math.cos(a)*(R-1));
      ctx.strokeStyle = (i % 9 === 0) ? '#888' : '#333';
      ctx.lineWidth   = (i % 9 === 0) ? 2 : 1;
      ctx.stroke();
    }
    // knob body with radial gradient
    const grad = ctx.createRadialGradient(CX-R*0.3, CY-R*0.3, 0, CX, CY, R-8);
    grad.addColorStop(0, '#666'); grad.addColorStop(1, '#1a1a1a');
    ctx.beginPath(); ctx.arc(CX, CY, R-8, 0, Math.PI*2);
    ctx.fillStyle = grad; ctx.fill();
    ctx.strokeStyle = '#333'; ctx.lineWidth = 1; ctx.stroke();
    // yellow indicator dot
    const rad = angle * Math.PI / 180;
    ctx.beginPath();
    ctx.arc(CX + Math.sin(rad)*(R-18), CY - Math.cos(rad)*(R-18), 5, 0, Math.PI*2);
    ctx.fillStyle = '#ff0'; ctx.fill();
    // center cap
    ctx.beginPath(); ctx.arc(CX, CY, 6, 0, Math.PI*2);
    ctx.fillStyle = '#333'; ctx.fill();
    ctx.strokeStyle = '#555'; ctx.lineWidth = 1; ctx.stroke();
  }

  function evAngle(e) {
    const rect = el.getBoundingClientRect();
    const cx = (e.touches ? e.touches[0].clientX : e.clientX) - rect.left - CX;
    const cy = (e.touches ? e.touches[0].clientY : e.clientY) - rect.top  - CY;
    return Math.atan2(cx, -cy) * 180 / Math.PI;
  }

  function tune(steps) {
    if (steps === 0) return;
    const step = parseInt(document.getElementById('step').value);
    const freq = state.mainFocused ? state.mainFreqHz : state.subFreqHz;
    const nf   = Math.max(0, freq + steps * step);
    sendCmd(`${state.mainFocused ? 'FA' : 'FB'}${nf.toString().padStart(9,'0')};`);
  }

  el.addEventListener('mousedown', e => {
    e.preventDefault(); dragging = true; accumDeg = 0;
    lastAng = evAngle(e); el.style.cursor = 'grabbing';
  });
  el.addEventListener('touchstart', e => {
    e.preventDefault(); dragging = true; accumDeg = 0; lastAng = evAngle(e);
  }, { passive:false });

  window.addEventListener('mousemove', e => {
    if (!dragging) return;
    const a = evAngle(e);
    let delta = a - lastAng;
    if (delta >  180) delta -= 360;
    if (delta < -180) delta += 360;
    lastAng   = a;
    angle    += delta;
    accumDeg += delta;
    draw();
    const steps = Math.trunc(accumDeg / DEG_PER_STEP);
    if (steps !== 0) { accumDeg -= steps * DEG_PER_STEP; tune(steps); }
  });
  window.addEventListener('touchmove', e => {
    if (!dragging) return; e.preventDefault();
    const a = evAngle(e);
    let delta = a - lastAng;
    if (delta >  180) delta -= 360;
    if (delta < -180) delta += 360;
    lastAng   = a;
    angle    += delta;
    accumDeg += delta;
    draw();
    const steps = Math.trunc(accumDeg / DEG_PER_STEP);
    if (steps !== 0) { accumDeg -= steps * DEG_PER_STEP; tune(steps); }
  }, { passive:false });

  window.addEventListener('mouseup',  () => { dragging = false; el.style.cursor = 'grab'; });
  window.addEventListener('touchend', () => { dragging = false; });

  // mouse wheel on knob
  el.addEventListener('wheel', e => {
    e.preventDefault();
    const dir = e.deltaY < 0 ? 1 : -1;
    angle += dir * DEG_PER_STEP;
    draw(); tune(dir);
  }, { passive:false });

  draw();
}

window.onload = () => {
  loadPorts(); connect(); loadAudioDevices(); connectAudioWs(); initKnob();
  document.querySelectorAll('input.vslider').forEach(sl => {
    sl.addEventListener('wheel', e => {
      e.preventDefault();
      const newVal = Math.min(parseInt(sl.max), Math.max(parseInt(sl.min), parseInt(sl.value) + (e.deltaY < 0 ? 1 : -1)));
      sl.value = newVal;
      sliderChange(sl.id, newVal);
    }, { passive: false });
  });
};
</script>
</body>
</html>
""";
} // end Resources class

// ── AudioEngine ───────────────────────────────────────────────────────────────
class AudioEngine
{
    private WaveInEvent?   _waveIn;
    private readonly ConcurrentDictionary<string, WebSocket> _audioClients = new();
    private bool           _running;
    public  Action<string>? Log { get; set; }

    // ── device enumeration ────────────────────────────────────────────────────
    public static List<(int index, string name)> GetDevices()
    {
        var list = new List<(int, string)>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var cap = WaveInEvent.GetCapabilities(i);
            list.Add((i, cap.ProductName));
        }
        return list;
    }

    // ── start capture ─────────────────────────────────────────────────────────
    public void Start(int deviceIndex)
    {
        Stop();
        try
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber       = deviceIndex,
                WaveFormat         = new WaveFormat(16000, 16, 1), // 16 kHz, 16-bit, mono
                BufferMilliseconds = 100
            };
            _waveIn.DataAvailable    += OnDataAvailable;
            _waveIn.RecordingStopped += (s, e) => { if (e.Exception != null) Log?.Invoke($"[Audio] Recording stopped: {e.Exception.Message}"); };
            _waveIn.StartRecording();
            _running = true;
            Log?.Invoke($"[Audio] Started capture from device {deviceIndex}: {WaveInEvent.GetCapabilities(deviceIndex).ProductName}");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[Audio] Start failed: {ex.Message}");
        }
    }

    // ── stop capture ──────────────────────────────────────────────────────────
    public void Stop()
    {
        if (_waveIn == null) return;
        try { _waveIn.StopRecording(); } catch { }
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.Dispose();
        _waveIn   = null;
        _running  = false;
        Log?.Invoke("[Audio] Capture stopped");
    }

    public bool IsRunning => _running;

    // ── WebSocket client management ───────────────────────────────────────────
    public void AddClient(string id, WebSocket ws)    => _audioClients[id] = ws;
    public void RemoveClient(string id)               => _audioClients.TryRemove(id, out _);

    // ── broadcast raw PCM to all audio listeners ──────────────────────────────
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _audioClients.IsEmpty) return;
        var seg  = new ArraySegment<byte>(e.Buffer, 0, e.BytesRecorded);
        var dead = new List<string>();
        foreach (var (id, ws) in _audioClients)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    ws.SendAsync(seg, WebSocketMessageType.Binary, true, CancellationToken.None)
                      .GetAwaiter().GetResult();
                else
                    dead.Add(id);
            }
            catch { dead.Add(id); }
        }
        foreach (var id in dead) _audioClients.TryRemove(id, out _);
    }
}

// ── RadioState ────────────────────────────────────────────────────────────────
class RadioState
{
    public bool   Connected   { get; set; }
    public string ComPort     { get; set; } = "";
    public string RadioModel  { get; set; } = "FTDX101D";
    public long   MainFreqHz  { get; set; }
    public long   SubFreqHz   { get; set; }
    public bool   MainFocused { get; set; } = true;
    public string MainMode    { get; set; } = "";
    public string SubMode     { get; set; } = "";
    public int    MainAnt     { get; set; } = 1;
    public int    SubAnt      { get; set; } = 1;
    public int    MainIpo     { get; set; }
    public int    SubIpo      { get; set; }
    public int    MainAtt     { get; set; }
    public int    SubAtt      { get; set; }
    public int    WidthPos    { get; set; } = 20;
    public int    ShiftSteps  { get; set; }
    public bool   Rx1Active   { get; set; }
    public bool   Rx2Active   { get; set; }
    public bool   NrOn        { get; set; }
    public bool   DnfOn       { get; set; }
    public bool   RfSqlOn     { get; set; }
    public string Temp        { get; set; } = "--\u00b0C";
    public string TempColor   { get; set; } = "cyan";
}

// ── RadioEngine ───────────────────────────────────────────────────────────────
class RadioEngine(RadioState state, JsonSerializerOptions jsonOpts,
                  ConcurrentDictionary<string, WebSocket> clients, int defaultBaud = 38400)
{
    public CancellationToken Stopping { get; set; } = CancellationToken.None;
    public Action<string>?  Log       { get; set; }

    private SerialPort? _port;
    private readonly object _lock = new();
    private string[] _pollCmds = BuildPollCmds(true);
    private int      _pollIndex;
    private long     _tuningUntil = 0; // ticks — poll is paused while tuning

    // ── connect / disconnect ──────────────────────────────────────────────────
    public void Connect(string portName)
    {
        lock (_lock) { try { _port?.Close(); } catch { } _port?.Dispose(); _port = null; }
        try
        {
            var sp = new SerialPort(portName, defaultBaud, Parity.None, 8, StopBits.Two)
            {
                Handshake    = Handshake.None,
                RtsEnable    = true,
                ReadTimeout  = 500,
                WriteTimeout = 500
            };
            sp.Open();
            lock (_lock) { _port = sp; }
            state.Connected = true;
            state.ComPort   = portName;

            // detect model
            string idResp = SendReceive("ID;");
            if (idResp.StartsWith("ID") && idResp.Length >= 4)
            {
                string num = idResp[2..].TrimStart('0');
                state.RadioModel = num == "682" ? "FTDX101MP" : "FTDX101D";
            }

            // initial full read of all parameters
            _pollIndex = 0;
            _pollCmds  = BuildPollCmds(state.MainFocused);
            foreach (var cmd in _pollCmds)
            {
                ProcessResponse(SendReceive(cmd));
                Thread.Sleep(6);
            }
            Log?.Invoke($"[Radio] Connected to {portName} — {state.RadioModel}");
        }
        catch (Exception ex)
        {
            state.Connected = false;
            Log?.Invoke($"[Serial] Connect failed: {ex.Message}");
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            try { _port?.Close(); } catch { }
            _port?.Dispose();
            _port = null;
            state.Connected = false;
        }
        Log?.Invoke("[Radio] Disconnected");
    }

    // ── poll loop ─────────────────────────────────────────────────────────────
    public async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_port == null || !_port.IsOpen) { await Task.Delay(1000, ct); continue; }

                // pause polling while user is tuning — avoids lock contention
                if (DateTime.UtcNow.Ticks < _tuningUntil) { await Task.Delay(80, ct); continue; }

                if (_pollIndex == 0)
                    lock (_lock) { try { _port.DiscardInBuffer(); _port.DiscardOutBuffer(); } catch { } }

                string resp = SendReceive(_pollCmds[_pollIndex]);
                try { ProcessResponse(resp); }
                catch (Exception ex) { Log?.Invoke($"[Parse] {_pollCmds[_pollIndex]}: {ex.Message}"); }

                _pollIndex = (_pollIndex + 1) % _pollCmds.Length;

                if (_pollIndex % 6 == 0)
                    try { await BroadcastStateAsync(); } catch { }

                await Task.Delay(80, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log?.Invoke($"[Poll] Error: {ex.Message} — restarting in 2s");
                await Task.Delay(2000, ct);
            }
        }
    }

    // ── WebSocket ─────────────────────────────────────────────────────────────
    public async Task SendStateAsync(WebSocket ws)
    {
        var seg = Serialize();
        await ws.SendAsync(seg, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public async Task ReceiveLoopAsync(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[4096];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try { result = await ws.ReceiveAsync(buf, ct); }
            catch { break; }
            if (result.MessageType == WebSocketMessageType.Close) break;
            HandleClientMessage(Encoding.UTF8.GetString(buf, 0, result.Count));
        }
    }

    private async Task BroadcastStateAsync()
    {
        var seg  = Serialize();
        var dead = new List<string>();
        foreach (var (id, ws) in clients)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(seg, WebSocketMessageType.Text, true, CancellationToken.None);
                else
                    dead.Add(id);
            }
            catch { dead.Add(id); }
        }
        foreach (var id in dead) clients.TryRemove(id, out _);
    }

    private ArraySegment<byte> Serialize()
    {
        var obj  = new { type = "state", data = state };
        return new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj, jsonOpts)));
    }

    // ── client message handler ────────────────────────────────────────────────
    private void HandleClientMessage(string msg)
    {
        try
        {
            using var doc = JsonDocument.Parse(msg);
            if (!doc.RootElement.TryGetProperty("cmd", out var el)) return;
            var cmd = el.GetString() ?? "";

            if (cmd.StartsWith("CONNECT:"))  { Connect(cmd[8..]); _ = BroadcastStateAsync(); return; }
            if (cmd == "DISCONNECT")         { Disconnect();       _ = BroadcastStateAsync(); return; }

            // frequency tuning
            if (cmd.StartsWith("FA") || cmd.StartsWith("FB"))
            {
                _tuningUntil = DateTime.UtcNow.AddMilliseconds(300).Ticks;
                SendCommand(cmd);
                // optimistic state update so display reacts instantly
                var digits = cmd[2..].TrimEnd(';');
                if (long.TryParse(digits, out long hz))
                {
                    if (cmd.StartsWith("FA")) state.MainFreqHz = hz;
                    else                      state.SubFreqHz  = hz;
                    _ = BroadcastStateAsync();
                }
                return;
            }

            SendCommand(cmd);
        }
        catch { }
    }

    // ── serial I/O
    private string SendReceive(string cmd)
    {
        lock (_lock)
        {
            if (_port == null || !_port.IsOpen) return "";
            try { _port.Write(cmd); Thread.Sleep(6); return _port.ReadTo(";"); }
            catch { return ""; }
        }
    }

    private void SendCommand(string cmd)
    {
        lock (_lock)
        {
            if (_port == null || !_port.IsOpen) return;
            try { _port.Write(cmd); Thread.Sleep(6); } catch { }
        }
    }

    // ── response parser (mirrors Form1.cs ProcessResponse exactly) ────────────
    private void ProcessResponse(string resp)
    {
        if (string.IsNullOrEmpty(resp)) return;

        if (resp.StartsWith("RM9") && resp.Length >= 6 && decimal.TryParse(resp[3..6], out var tn))
        {
            var t = decimal.Floor(tn / 2.3m - 6);
            state.Temp      = $"{t:00}\u00b0C";
            state.TempColor = t > 40 ? "red" : t > 33 ? "orange" : "cyan";
        }
        else if (resp.StartsWith("EX030107") && resp.Length >= 9)
            state.RfSqlOn = resp[8] == '1';

        else if (resp.StartsWith("SH") && resp.Length >= 6
                 && (resp[2] == '0') == state.MainFocused
                 && int.TryParse(resp[4..6], out int shv))
            state.WidthPos = shv;

        else if (resp.StartsWith("IS") && resp.Length >= 9
                 && (resp[2] == '0') == state.MainFocused
                 && int.TryParse(resp[5..9], out int isAbs))
            state.ShiftSteps = (resp[4] == '-' ? -isAbs : isAbs) / 20;

        else if (resp.StartsWith("MD") && resp.Length >= 4 && (resp[2] == '0' || resp[2] == '1'))
        {
            if (resp[2] == '0') state.MainMode = resp[3].ToString();
            else                state.SubMode  = resp[3].ToString();
        }
        else if (resp.StartsWith("AN") && resp.Length >= 4 && (resp[2] == '0' || resp[2] == '1')
                 && (resp[2] == '0') == state.MainFocused)
        {
            int a = resp[3] - '0';
            if (resp[2] == '0') state.MainAnt = a; else state.SubAnt = a;
        }
        else if (resp.StartsWith("PA") && resp.Length >= 4 && (resp[2] == '0' || resp[2] == '1')
                 && (resp[2] == '0') == state.MainFocused)
        {
            int p = resp[3] - '0';
            if (resp[2] == '0') state.MainIpo = p; else state.SubIpo = p;
        }
        else if (resp.StartsWith("RA") && resp.Length >= 4 && (resp[2] == '0' || resp[2] == '1')
                 && (resp[2] == '0') == state.MainFocused)
        {
            int a = resp[3] - '0';
            if (resp[2] == '0') state.MainAtt = a; else state.SubAtt = a;
        }
        else if (resp.StartsWith("NR") && resp.Length >= 4 && (resp[2] == '0') == state.MainFocused)
            state.NrOn = resp[3] == '1';

        else if (resp.StartsWith("BC") && resp.Length >= 4 && (resp[2] == '0') == state.MainFocused)
            state.DnfOn = resp[3] == '1';

        else if (resp.StartsWith("FR") && resp.Length >= 4)
        {
            state.Rx1Active = resp == "FR00" || resp == "FR01";
            state.Rx2Active = resp == "FR00" || resp == "FR10";
        }
        else if (resp.StartsWith("FA") && resp.Length >= 4
                 && long.TryParse(resp[2..(resp.Length - 1)], out long fa))
            state.MainFreqHz = fa * 10;

        else if (resp.StartsWith("FB") && resp.Length >= 4
                 && long.TryParse(resp[2..(resp.Length - 1)], out long fb))
            state.SubFreqHz = fb * 10;

        else if (resp.StartsWith("VS") && resp.Length >= 3)
        {
            state.MainFocused = resp[2] == '0';
            _pollCmds = BuildPollCmds(state.MainFocused);
            _pollIndex = 0;
        }
    }

    // ── poll command list (mirrors Form1.cs RebuildPollCommands) ─────────────
    private static string[] BuildPollCmds(bool main) => [
        "FA;", "FB;",
        "RM9;", "EX030107;",
        "FA;", "FB;",
        main?"MD0;":"MD1;",
        "FA;", "FB;",
        main?"AN0;":"AN1;", main?"PA0;":"PA1;", main?"RA0;":"RA1;",
        main?"NR0;":"NR1;", main?"BC0;":"BC1;", "FR;",
        "FA;", "FB;",
        main?"SH0;":"SH1;", main?"IS0;":"IS1;",
        "VS;"
    ];
}
