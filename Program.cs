using System.Collections.Concurrent;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

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

// ── log ring buffer ───────────────────────────────────────────────────────────
var logLines = new List<string>();
var logLock  = new object();
void AppLog(string msg) {
    lock (logLock) { logLines.Add($"[{DateTime.Now:HH:mm:ss}] {msg}"); if (logLines.Count > 8) logLines.RemoveAt(0); }
}
engine.Log = AppLog;

// ── ASP.NET Core minimal API ──────────────────────────────────────────────────
var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls($"http://*:{httpPort}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.MapGet("/", () => Results.Content(Resources.HtmlPage, "text/html"));

app.MapGet("/api/ports", () =>
    Results.Json(SerialPort.GetPortNames().OrderBy(p => p, StringComparer.Ordinal)));

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
app.Lifetime.ApplicationStopping.Register(() => cts.Cancel());

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

await app.RunAsync();
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
        L($"  Volume  :  {StVal(st.Volume),3}   RF Gain: {StVal(st.RfGain),3}   Power: {st.Power}W");
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

#conn-bar { display:flex; align-items:center; gap:8px; padding:5px 10px; background:#1a1a1a; border-bottom:2px solid #333; }
#conn-bar.hidden { display:none; }
#conn-label { color:#ff4444; font-weight:bold; }
#conn-bar select { background:#222; color:#ccc; border:1px solid #555; padding:3px 6px; }
#conn-bar button { background:#333; color:#ccc; border:1px solid #555; padding:3px 10px; cursor:pointer; }
#conn-bar button:hover { background:#555; }
#radio-info { margin-left:auto; color:#888; font-size:11px; }

#app { display:flex; gap:6px; padding:6px; }
#controls { flex:1; display:flex; flex-direction:column; gap:4px; min-width:580px; }
#sliders { display:flex; gap:4px; }

/* Frequency */
#freq-section { display:flex; align-items:center; gap:8px; background:#000; padding:4px 8px; border:1px solid #333; }
.freq-box {
  font-family:monospace; font-size:24px; font-weight:bold;
  padding:3px 10px; cursor:pointer; min-width:170px; text-align:center;
  border:2px solid #555; letter-spacing:2px;
}
#freq-m { background:black; color:gold; }
#freq-s { background:black; color:gold; font-size:17px; min-width:150px; }
#band-col { display:flex; flex-direction:column; align-items:center; gap:3px; }
#band { background:#222; color:cyan; padding:3px 10px; font-size:15px; font-weight:bold;
        border:1px solid #555; min-width:55px; text-align:center; }
#temp-val { font-size:11px; text-align:center; color:cyan; }
#freq-controls { display:flex; align-items:center; gap:4px; margin-left:4px; }
#freq-controls select { background:#222; color:#ccc; border:1px solid #444; padding:3px 4px; font-size:11px; }

/* Buttons */
.btn-row { display:flex; gap:3px; flex-wrap:wrap; align-items:center; padding:2px 0; }
.btn {
  background:#006400; color:#ff0; border:2px solid #fff;
  font-family:Verdana,sans-serif; font-size:11px; font-weight:bold;
  padding:4px 5px; cursor:pointer; min-width:44px; min-height:34px;
  text-align:center; line-height:1.2;
}
.btn:hover { background:#00008b; }
.btn.active { background:#8b0000; }
.btn:active { background:#cc0000; }

#btn-rx1 { background:silver; color:#00008b; border-color:#aaa; }
#btn-rx1.active { background:#8b0000; color:#ff0; border-color:#fff; }
#btn-rx2 { background:#00008b; color:silver; border-color:#aaa; }
#btn-rx2.active { background:#8b0000; color:#ff0; border-color:#fff; }

.sep { width:1px; background:#444; align-self:stretch; margin:0 3px; }
.slbl { font-size:10px; color:#666; white-space:nowrap; }

/* LEV */
#lev-section { display:flex; align-items:center; gap:5px; }
#lev-val { font-family:monospace; font-size:13px; min-width:80px; text-align:center;
           color:limegreen; background:#000; padding:2px 6px; border:1px solid #444; }

/* Sliders */
.slider-col { display:flex; flex-direction:column; align-items:center; gap:2px;
              padding:5px 4px; background:#1a1a1a; border:1px solid #333; min-width:40px; }
.slider-col label { font-size:10px; color:#999; text-align:center; line-height:1.2; }
.slider-col .val { font-family:monospace; font-size:11px; color:gold; }
input[type=range].vslider { writing-mode:vertical-lr; direction:rtl; width:28px; height:115px; cursor:pointer; accent-color:#888; }
#sl-pwr { accent-color:#cc0000; }
.pwr-col { border-color:#440000; }
</style>
</head>
<body>

<!-- Connection bar (hidden when connected) -->
<div id="conn-bar">
  <span id="conn-label">&#9888; NOT CONNECTED</span>
  <select id="port-sel"><option>Loading...</option></select>
  <button onclick="connectToPort()">Connect</button>
  <button onclick="disconnectPort()">Disconnect</button>
  <button onclick="loadPorts()">&#8635; Refresh ports</button>
  <span id="radio-info"></span>
</div>

<!-- Main UI -->
<div id="app">
  <div id="controls">

    <!-- FREQUENCY ROW -->
    <div id="freq-section">
      <div>
        <div id="freq-m" class="freq-box" onclick="sendCmd('VS0;')" title="Click to focus Main VFO"> .   .   </div>
        <div id="freq-s" class="freq-box" onclick="sendCmd('VS1;')" title="Click to focus Sub VFO"> .   .   </div>
      </div>
      <div id="band-col">
        <div id="band">---</div>
        <div id="temp-val">--&#176;C</div>
      </div>
      <div id="freq-controls">
        <select id="step">
          <option value="100">100 Hz</option>
          <option value="500">500 Hz</option>
          <option value="1000" selected>1 kHz</option>
          <option value="5000">5 kHz</option>
          <option value="9000">9 kHz</option>
          <option value="20000">20 kHz</option>
          <option value="50000">50 kHz</option>
        </select>
        <button class="btn" onclick="freqStep(1)">+</button>
        <button class="btn" onclick="freqStep(-1)">&minus;</button>
        <button class="btn" oncontextmenu="bandStep(-1);return false;"
                onclick="bandStep(1)" title="Click=Band up  Right-click=Band down">BAND</button>
      </div>
      <button class="btn" onclick="sendCmd('SV;')" style="margin-left:8px;">SWAP</button>
    </div>

    <!-- MODE + SCOPE + SPAN -->
    <div class="btn-row">
      <button class="btn" id="btn-usb"  onclick="sendCmd(modeCmd('2'))">USB</button>
      <button class="btn" id="btn-lsb"  onclick="sendCmd(modeCmd('1'))">LSB</button>
      <button class="btn" id="btn-cw"   onclick="sendCmd(modeCmd('3'))">CW</button>
      <button class="btn" id="btn-am"   onclick="sendCmd(modeCmd('5'))">AM</button>
      <button class="btn" id="btn-fm"   onclick="sendCmd(modeCmd('4'))">FM</button>
      <button class="btn" id="btn-dig"  onclick="sendCmd(modeCmd('C'))">DIG</button>
      <div class="sep"></div>
      <button class="btn" id="btn-center" onclick="scopeCmd('center')">CENTER</button>
      <button class="btn" id="btn-cursor" onclick="scopeCmd('cursor')">CURSOR</button>
      <button class="btn" id="btn-fix"    onclick="scopeCmd('fix')">FIX</button>
      <div class="sep"></div>
      <span class="slbl">SPAN:</span>
      <button class="btn" id="btn-ssb1" onclick="ssbCmd(1)">SSB1</button>
      <button class="btn" id="btn-ssb2" onclick="ssbCmd(2)">SSB2</button>
      <button class="btn" id="btn-ssb3" onclick="ssbCmd(3)">SSB3</button>
      <button class="btn" id="btn-ssb4" onclick="ssbCmd(4)">SSB4</button>
      <button class="btn" id="btn-ssb5" onclick="ssbCmd(5)">SSB5</button>
      <button class="btn" id="btn-ssb6" onclick="ssbCmd(6)">SSB6</button>
    </div>

    <!-- ANT + AMP + ATT -->
    <div class="btn-row">
      <span class="slbl">ANT:</span>
      <button class="btn" id="btn-ant1" onclick="sendCmd(antCmd(1))">ANT1</button>
      <button class="btn" id="btn-ant2" onclick="sendCmd(antCmd(2))">ANT2</button>
      <button class="btn" id="btn-ant3" onclick="sendCmd(antCmd(3))">ANT3</button>
      <div class="sep"></div>
      <span class="slbl">AMP:</span>
      <button class="btn" id="btn-ipo"  onclick="sendCmd(ipoCmd(0))">IPO</button>
      <button class="btn" id="btn-amp1" onclick="sendCmd(ipoCmd(1))">AMP1</button>
      <button class="btn" id="btn-amp2" onclick="sendCmd(ipoCmd(2))">AMP2</button>
      <div class="sep"></div>
      <span class="slbl">ATT:</span>
      <button class="btn" id="btn-att0"  onclick="sendCmd(attCmd(0))">0 dB</button>
      <button class="btn" id="btn-att6"  onclick="sendCmd(attCmd(1))">6 dB</button>
      <button class="btn" id="btn-att12" onclick="sendCmd(attCmd(2))">12 dB</button>
      <button class="btn" id="btn-att18" onclick="sendCmd(attCmd(3))">18 dB</button>
    </div>

    <!-- RX + NR + DNF + SQL + TUNER -->
    <div class="btn-row">
      <button class="btn" id="btn-rx1" onclick="rxCmd('rx1')"
              oncontextmenu="sendCmd('MUTEBOTH');return false;"
              title="Click=toggle mute RX1  Right-click=mute/unmute both">RX1</button>
      <button class="btn" id="btn-rx2" onclick="rxCmd('rx2')"
              oncontextmenu="sendCmd('MUTEBOTH');return false;"
              title="Click=toggle mute RX2  Right-click=mute/unmute both">RX2</button>
      <button class="btn" id="btn-nr"    onclick="nrCmd()">NR</button>
      <button class="btn" id="btn-dnf"   onclick="dnfCmd()">DNF</button>
      <button class="btn" id="btn-rfsql" onclick="rfSqlCmd()">RF/SQL<br>MAIN</button>
      <div class="sep"></div>
      <span class="slbl">TUNER:</span>
      <button class="btn" id="btn-inttune"   onclick="sendCmd('AC001;');sendCmd('AC002;')">Int<br>Tune</button>
      <button class="btn" id="btn-itune-on"  onclick="sendCmd('AC001;')">Tune<br>ON</button>
      <button class="btn" id="btn-itune-off" onclick="sendCmd('AC000;')">Tune<br>OFF</button>
      <button class="btn" id="btn-exttune"
              onmousedown="extTuneDown()" onmouseup="extTuneUp()"
              ontouchstart="extTuneDown();return false;" ontouchend="extTuneUp()">Ext<br>Tuner</button>
    </div>

    <!-- LEV -->
    <div id="lev-section">
      <span class="slbl">LEVEL SHIFT:</span>
      <button class="btn" onclick="sendCmd('LEV-')" style="min-width:30px;">&minus;</button>
      <div id="lev-val">+00.0 dB</div>
      <button class="btn" onclick="sendCmd('LEV+')" style="min-width:30px;">+</button>
      <button class="btn" onclick="sendCmd('LEVRESET')" style="min-width:40px;">RESET</button>
    </div>

  </div><!-- #controls -->

  <!-- SLIDERS -->
  <div id="sliders">
    <div class="slider-col">
      <label>RF<br>GAIN</label>
      <input type="range" class="vslider" id="sl-rfgain" min="0" max="255" value="255"
             oninput="sliderChange('sl-rfgain',this.value)">
      <span class="val" id="sl-rfgain-val">100</span>
    </div>
    <div class="slider-col">
      <label>VOL</label>
      <input type="range" class="vslider" id="sl-vol" min="0" max="255" value="0"
             oninput="sliderChange('sl-vol',this.value)">
      <span class="val" id="sl-vol-val">000</span>
    </div>
    <div class="slider-col">
      <label>SRF<br>GAIN</label>
      <input type="range" class="vslider" id="sl-srfgain" min="0" max="255" value="255"
             oninput="sliderChange('sl-srfgain',this.value)">
      <span class="val" id="sl-srfgain-val">100</span>
    </div>
    <div class="slider-col">
      <label>SVOL</label>
      <input type="range" class="vslider" id="sl-svol" min="0" max="255" value="0"
             oninput="sliderChange('sl-svol',this.value)">
      <span class="val" id="sl-svol-val">000</span>
    </div>
    <div class="slider-col pwr-col">
      <label>PWR</label>
      <input type="range" class="vslider" id="sl-pwr" min="5" max="100" value="100"
             oninput="sliderChange('sl-pwr',this.value)">
      <span class="val" id="sl-pwr-val">100</span>
    </div>
  </div><!-- #sliders -->

</div><!-- #app -->

<script>
let ws = null;
let state = { connected:false, mainFocused:true, rx1Active:false, rx2Active:false,
  nrOn:false, dnfOn:false, rfSqlOn:false, iTuneOn:false,
  mainFreqHz:0, subFreqHz:0, mainMode:'2', subMode:'2',
  mainAnt:1, subAnt:1, mainIpo:0, subIpo:0, mainAtt:0, subAtt:0,
  rfGain:255, subRfGain:255, volume:0, subVolume:0, power:100,
  scopeMode:'center', dspSpan:'SSB1', levelShift:0,
  temp:'--\u00b0C', tempColor:'cyan', radioModel:'FTDX101D', maxPower:100 };
const timers = {};

// ── WebSocket ─────────────────────────────────────────────────────────────────
function connect() {
  ws = new WebSocket(`ws://${location.host}/ws`);
  ws.onmessage = e => { const m = JSON.parse(e.data); if (m.type==='state') applyState(m.data); };
  ws.onclose   = () => setTimeout(connect, 2000);
  ws.onerror   = () => ws.close();
}
function sendCmd(cmd) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({cmd}));
}

// ── State → UI ────────────────────────────────────────────────────────────────
function applyState(s) {
  state = s;
  document.getElementById('conn-bar').style.display = s.connected ? 'none' : 'flex';
  document.getElementById('radio-info').textContent  = s.connected ? `${s.radioModel}  @  ${s.comPort}` : '';

  updateFreqs(s);
  document.getElementById('band').textContent = bandName(s.mainFocused ? s.mainFreqHz : s.subFreqHz);

  const tv = document.getElementById('temp-val');
  tv.textContent = s.temp; tv.style.color = s.tempColor;

  setA('btn-usb',  s.mainMode==='2');
  setA('btn-lsb',  s.mainMode==='1');
  setA('btn-cw',   s.mainMode==='3');
  setA('btn-am',   s.mainMode==='5');
  setA('btn-fm',   s.mainMode==='4');
  setA('btn-dig',  s.mainMode==='C');

  setA('btn-center', s.scopeMode==='center');
  setA('btn-cursor', s.scopeMode==='cursor');
  setA('btn-fix',    s.scopeMode==='fix');

  setA('btn-ssb1', s.dspSpan==='SSB1'); setA('btn-ssb2', s.dspSpan==='SSB2');
  setA('btn-ssb3', s.dspSpan==='SSB3'); setA('btn-ssb4', s.dspSpan==='SSB4');
  setA('btn-ssb5', s.dspSpan==='SSB5'); setA('btn-ssb6', s.dspSpan==='SSB6');

  setA('btn-ant1', s.mainAnt===1); setA('btn-ant2', s.mainAnt===2); setA('btn-ant3', s.mainAnt===3);
  setA('btn-ipo',  s.mainIpo===0); setA('btn-amp1', s.mainIpo===1); setA('btn-amp2', s.mainIpo===2);
  setA('btn-att0', s.mainAtt===0); setA('btn-att6',  s.mainAtt===1);
  setA('btn-att12',s.mainAtt===2); setA('btn-att18', s.mainAtt===3);

  setA('btn-rx1',      s.rx1Active);  setA('btn-rx2',       s.rx2Active);
  setA('btn-nr',       s.nrOn);       setA('btn-dnf',       s.dnfOn);
  setA('btn-rfsql',    s.rfSqlOn);
  setA('btn-itune-on', s.iTuneOn);    setA('btn-itune-off', !s.iTuneOn);
  document.getElementById('btn-rfsql').innerHTML = s.rfSqlOn ? 'SQUELCH<br>MAIN' : 'RF/SQL<br>MAIN';

  setSl('sl-rfgain',  s.rfGain,    toDisp(s.rfGain));
  setSl('sl-vol',     s.volume,    toDisp(s.volume));
  setSl('sl-pwr',     s.power,     s.power.toString().padStart(3,'0'));
  setSl('sl-srfgain', s.subRfGain, toDisp(s.subRfGain));
  setSl('sl-svol',    s.subVolume, toDisp(s.subVolume));
  document.getElementById('sl-pwr').max = s.maxPower;

  const lv = document.getElementById('lev-val');
  lv.textContent = fmtLev(s.levelShift) + ' dB';
  lv.style.color = s.levelShift < 0 ? 'red' : 'limegreen';
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
function toDisp(v) { return Math.round(v/255*100).toString().padStart(3,'0'); }
function fmtLev(v) { return (v>=0?'+':'-') + Math.abs(v).toFixed(1).padStart(4,'0'); }
function bandName(hz) {
  if(hz>=1810000&&hz<1900000)  return '160m'; if(hz>=3500000&&hz<3800000)  return '80m';
  if(hz>=5350000&&hz<5367000)  return '60m';  if(hz>=7000000&&hz<7200000)  return '40m';
  if(hz>=10100000&&hz<10150000)return '30m';  if(hz>=14000000&&hz<14350000)return '20m';
  if(hz>=18068000&&hz<18168000)return '17m';  if(hz>=21000000&&hz<21450000)return '15m';
  if(hz>=24890000&&hz<24990000)return '12m';  if(hz>=28000000&&hz<29700000)return '10m';
  if(hz>=50000000&&hz<54000000)return '6m';   return '---';
}

// ── CAT command builders ──────────────────────────────────────────────────────
const vfo    = ()  => state.mainFocused ? '0' : '1';
const modeCmd= m   => `MD${vfo()}${m};`;
const antCmd = n   => `AN${vfo()}${n};`;
const ipoCmd = n   => `PA${vfo()}${n};`;
const attCmd = n   => `RA${vfo()}${n};`;

function scopeCmd(mode) {
  const v=vfo();
  sendCmd(`SS${v}650000;`);
  if (mode==='cursor') sendCmd(`SS${v}680000;`);
  else if (mode==='fix') sendCmd(`SS${v}6B0000;`);
}
function ssbCmd(n) {
  const c={1:'56',2:'57',3:'58',4:'59',5:'54',6:'55'};
  sendCmd(`SS${vfo()}${c[n]}0000;`);
}
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

// External tuner — press-and-hold (server handles mode/power save+restore)
function extTuneDown() { sendCmd('EXTTUNE_DOWN'); }
function extTuneUp()   { sendCmd('EXTTUNE_UP'); }

// Sliders with 150 ms debounce
function sliderChange(id, raw) {
  const v=parseInt(raw);
  const dv=document.getElementById(id+'-val');
  if (dv) dv.textContent = id==='sl-pwr' ? v.toString().padStart(3,'0') : toDisp(v);
  clearTimeout(timers[id]);
  timers[id]=setTimeout(()=>{
    let cmd;
    if      (id==='sl-rfgain')  cmd=`RG0${(255-v).toString().padStart(3,'0')};`;
    else if (id==='sl-vol')     cmd=`AG0${v.toString().padStart(3,'0')};`;
    else if (id==='sl-pwr')     cmd=`PC${v.toString().padStart(3,'0')};`;
    else if (id==='sl-srfgain') cmd=`RG1${(255-v).toString().padStart(3,'0')};`;
    else if (id==='sl-svol')    cmd=`AG1${v.toString().padStart(3,'0')};`;
    if (cmd) sendCmd(cmd);
  },150);
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

window.onload = () => { loadPorts(); connect(); };
</script>
</body>
</html>
""";
} // end Resources class

// ── RadioState ────────────────────────────────────────────────────────────────
class RadioState
{
    public bool   Connected   { get; set; }
    public string ComPort     { get; set; } = "";
    public string RadioModel  { get; set; } = "FTDX101D";
    public int    MaxPower    { get; set; } = 100;
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
    public int    RfGain      { get; set; } = 255;
    public int    SubRfGain   { get; set; } = 255;
    public int    Volume      { get; set; }
    public int    SubVolume   { get; set; }
    public int    Power       { get; set; } = 100;
    public bool   Rx1Active   { get; set; }
    public bool   Rx2Active   { get; set; }
    public bool   NrOn        { get; set; }
    public bool   DnfOn       { get; set; }
    public bool   RfSqlOn     { get; set; }
    public bool   ITuneOn     { get; set; }
    public string ScopeMode   { get; set; } = "center";
    public string DspSpan     { get; set; } = "SSB1";
    public double LevelShift  { get; set; }
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

    // saved for external tuner restore
    private string _savedMode  = "";
    private string _savedPower = "";

    // saved for mute-both restore
    private int  _savedMainVol;
    private int  _savedSubVol;
    private bool _bothMuted;

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
                state.MaxPower   = num == "682" ? 200 : 100;
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

            if (cmd == "LEV+")     { state.LevelShift = Math.Min(30, state.LevelShift + 1); SendLev(); return; }
            if (cmd == "LEV-")     { state.LevelShift = Math.Max(-30, state.LevelShift - 1); SendLev(); return; }
            if (cmd == "LEVRESET") { state.LevelShift = 0; SendLev(); return; }

            if (cmd == "MUTEBOTH")
            {
                if (!_bothMuted)
                {
                    _savedMainVol = state.Volume;
                    _savedSubVol  = state.SubVolume;
                    SendCommand("AG0000;");
                    SendCommand("AG1000;");
                    _bothMuted = true;
                }
                else
                {
                    SendCommand($"AG0{_savedMainVol:D3};");
                    SendCommand($"AG1{_savedSubVol:D3};");
                    _bothMuted = false;
                }
                return;
            }

            if (cmd == "EXTTUNE_DOWN")
            {
                if (state.ITuneOn) return;
                _savedMode  = SendReceive("MD0;");
                var pr      = SendReceive("PC;");
                _savedPower = pr.Length >= 5 ? pr[2..5] : "100";
                SendCommand("PC010;");
                SendCommand("MD05;");
                SendCommand("MX1;");
                return;
            }
            if (cmd == "EXTTUNE_UP")
            {
                SendCommand("MX0;");
                if (!string.IsNullOrEmpty(_savedMode))  SendCommand(_savedMode  + ";");
                if (!string.IsNullOrEmpty(_savedPower)) SendCommand("PC" + _savedPower + ";");
                return;
            }

            SendCommand(cmd);
        }
        catch { }
    }

    private void SendLev()
    {
        var v = state.LevelShift;
        var fmt = v.ToString("+00.0;-00.0", System.Globalization.CultureInfo.InvariantCulture);
        SendCommand($"SS{(state.MainFocused ? 0 : 1)}4{fmt};");
    }

    // ── serial I/O ────────────────────────────────────────────────────────────
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

        else if ((resp.StartsWith("SS06") || resp.StartsWith("SS16")) && resp.Length >= 5
                 && (resp[2] == '0') == state.MainFocused)
            state.ScopeMode = resp[4] == '8' ? "cursor" : resp[4] == '5' ? "center" : "fix";

        else if ((resp.StartsWith("SS05") || resp.StartsWith("SS15")) && resp.Length >= 5
                 && (resp[2] == '0') == state.MainFocused)
            state.DspSpan = resp[4] switch
            {
                '6' => "SSB1", '7' => "SSB2", '8' => "SSB3",
                '9' => "SSB4", '4' => "SSB5", '5' => "SSB6",
                _   => state.DspSpan
            };

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
        else if (resp.StartsWith("RG0") && resp.Length >= 6 && int.TryParse(resp[3..6], out int rg0))
            state.RfGain = 255 - rg0;

        else if (resp.StartsWith("AG0") && resp.Length >= 6 && int.TryParse(resp[3..6], out int ag0))
            state.Volume = ag0;

        else if (resp.StartsWith("PC") && resp.Length >= 5 && int.TryParse(resp[2..5], out int pc))
            state.Power = pc;

        else if (resp.StartsWith("RG1") && resp.Length >= 6 && int.TryParse(resp[3..6], out int rg1))
            state.SubRfGain = 255 - rg1;

        else if (resp.StartsWith("AG1") && resp.Length >= 6 && int.TryParse(resp[3..6], out int ag1))
            state.SubVolume = ag1;

        else if (resp.StartsWith("FA") && resp.Length >= 4
                 && long.TryParse(resp[2..(resp.Length - 1)], out long fa))
            state.MainFreqHz = fa * 10;

        else if (resp.StartsWith("FB") && resp.Length >= 4
                 && long.TryParse(resp[2..(resp.Length - 1)], out long fb))
            state.SubFreqHz = fb * 10;

        else if (resp.StartsWith("AC") && resp.Length >= 5)
            state.ITuneOn = resp[4] == '1';

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
        main?"SS06;":"SS16;", main?"SS05;":"SS15;", main?"MD0;":"MD1;", main?"SS04;":"SS14;",
        "FA;", "FB;",
        main?"AN0;":"AN1;", main?"PA0;":"PA1;", main?"RA0;":"RA1;",
        main?"NR0;":"NR1;", main?"BC0;":"BC1;", "FR;",
        "FA;", "FB;",
        "RG0;", "AG0;", "PC;",
        "FA;", "FB;",
        "RG1;", "AG1;", "AC;", "VS;"
    ];
}
