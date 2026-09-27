/*
 * Claude Usage - iCUE widget for the CORSAIR XENEON EDGE.
 *
 * Subscribes to the document ClaudeStatus publishes on this PC over a WebSocket
 * (docs/icue-widget.md for the contract) and draws it. The socket goes to the
 * IPv6 loopback address: iCUE refuses a widget "localhost" and "127.0.0.1",
 * over HTTP and WebSocket alike, and lets ws://[::1] through. The app pushes
 * the moment a reading changes, so nothing polls. Plain fetch stays as the
 * last resort, for a browser on the desk. Nothing here talks to anything but
 * this machine; the only outbound link is the download page, opened through
 * iCUE's link plugin when ClaudeStatus is not running.
 *
 * The iCUE bridge (icueEvents), the reader of iCUE's settings (icueLocal) and
 * the boot call live inline in index.html: that is where iCUE's variables are
 * visible and where its validator looks. Everything they call is defined here.
 */

var DOWNLOAD_URL = "https://github.com/FrancMunoz/claude-status/releases/latest";
var DEFAULT_PORT = 47831;

function getIcueProperty(name) {
	// The inline script in index.html sees what iCUE injects; ask it first.
	if (typeof icueLocal === "function") {
		try {
			var inline = icueLocal(name);
			if (inline !== undefined && inline !== null && inline !== "") { return inline; }
		} catch (e) { /* fall through */ }
	}
	if (typeof window !== "undefined" && Object.prototype.hasOwnProperty.call(window, name)) {
		var v = window[name];
		if (v !== undefined && v !== null && v !== "") { return v; }
	}
	try {
		var local = Function("return typeof " + name + " !== 'undefined' ? " + name + " : undefined")();
		if (local !== undefined && local !== null && local !== "") { return local; }
	} catch (e) { /* not injected */ }
	return undefined;
}
function clamp(v, lo, hi, d) { v = Number(v); return Number.isFinite(v) ? Math.max(lo, Math.min(hi, v)) : d; }

// ---- strings: English here, translation.json carries the rest, tr() picks ----
var S = {
	title: "CLAUDE STATUS", session: "5H SESSION", week: "7D", weekSub: "ALL MODELS", fableSub: "FABLE",
	sessions: "SESSIONS",
	live: "LIVE", stale: "STALE", offline: "OFFLINE", paceChip: "PACE", preview: "PREVIEW",
	resetsIn: "resets in", resetsAt: "resets", left: "left",
	windowSession: "5h", windowWeek: "7d",
	paceOk: "on course for ~{p}%",
	paceHigh: "heading for ~{p}%",
	paceOut: "At this pace the {w} window runs out at {t}, {d} before it resets.",
	appOffline: "ClaudeStatus is not running",
	appOfflineHint: "Start ClaudeStatus and turn on \"Share the reading with the iCUE widget\" in Config > Behaviour (port {port}).",
	noCredential: "Not signed in",
	noCredentialHint: "Sign in to Claude Code on this PC; ClaudeStatus reads its login. Tap to open Config.",
	unreachable: "Claude unreachable",
	unreachableHint: "ClaudeStatus has not reached the usage endpoint yet.",
	noData: "Waiting for the first reading",
	retry: "Retry", reload: "Reload widget", download: "Get ClaudeStatus", retrying: "Retrying…"
};

// The English originals, kept apart so a second pass (iCUE re-initialising the
// widget) looks up the English key again rather than an already translated one.
var S_EN = Object.assign({}, S);
async function loadStrings() {
	if (typeof tr !== "function") { return; }
	var keys = Object.keys(S_EN);
	var values = await Promise.all(keys.map(function (k) { return tr(S_EN[k]); }));
	keys.forEach(function (k, i) { if (typeof values[i] === "string" && values[i]) { S[k] = values[i]; } });
}

// ---- state ----
var snapshot = null;      // the last document ClaudeStatus sent
var lastGood = null;      // the last document that carried windows
var appReachable = true;  // whether the app is answering at all
var lastError = "";       // what the browser said the last time it could not
var tickTimer = null;
var mock = null;          // sample data: the iCUE selector preview, ?mock=<state> in a browser, or a MOCK_STATE global set by a host page
try { mock = (typeof MOCK_STATE === "string" && MOCK_STATE) || new URLSearchParams(location.search).get("mock"); } catch (e) { /* no query string */ }

var els = {};
["root", "header", "chips", "titleText", "sessions", "sessionDots", "sessionsCount", "sessionsLabel", "ring", "ringUsage", "ringTime",
	"heroValue", "heroLabel", "heroReset", "heroResetLabel", "rows", "notice", "noticeText", "state", "stateBig", "stateSmall", "stateDiag", "stateActions", "retryButton", "reloadButton", "downloadButton"]
	.forEach(function (id) { els[id] = document.getElementById(id); });

var RING_R_USAGE = 44, RING_R_TIME = 35.5;
function circ(r) { return 2 * Math.PI * r; }
els.ringUsage.style.strokeDasharray = circ(RING_R_USAGE); els.ringUsage.style.strokeDashoffset = circ(RING_R_USAGE);
els.ringTime.style.strokeDasharray = circ(RING_R_TIME); els.ringTime.style.strokeDashoffset = circ(RING_R_TIME);

// ---- helpers ----
function fmt(s, vars) { return s.replace(/\{(\w+)\}/g, function (_, k) { return vars[k] === undefined ? "" : vars[k]; }); }
function pad2(n) { return String(n).padStart(2, "0"); }
function hmm(ms) { var m = Math.max(0, Math.round(ms / 60000)); return Math.floor(m / 60) + ":" + pad2(m % 60); }
function dh(ms) { var h = Math.max(0, Math.floor(ms / 3600000)); return Math.floor(h / 24) + "d " + (h % 24) + "h"; }
function clock(d) { return pad2(d.getHours()) + ":" + pad2(d.getMinutes()); }
function weekday(d) {
	var w = d.toLocaleDateString(undefined, { weekday: "short" }).replace(".", "");
	return w.charAt(0).toUpperCase() + w.slice(1);
}
function esc(s) { return String(s).replace(/[&<>"]/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;" }[c]; }); }
function timePercent(w, now) {
	var span = (w.spanSeconds || 0) * 1000; if (!span || !w.resetsAt) { return 0; }
	var left = new Date(w.resetsAt).getTime() - now; return clamp(100 * (1 - left / span), 0, 100, 0);
}
function resetText(w, now) {
	if (!w.resetsAt) { return null; }
	var left = new Date(w.resetsAt).getTime() - now;
	if (left < 86400000) { return { num: hmm(left), lbl: S.left, soon: true }; }
	var d = new Date(w.resetsAt); return { num: weekday(d) + " " + clock(d), lbl: dh(left), soon: false };
}
// Linear projection from spend against elapsed time: where the window lands at reset.
function paceText(w, now) {
	var tp = timePercent(w, now); if (tp < 8 || w.percent <= 0 || w.exhausted) { return { text: "", bad: false }; }
	var projected = Math.round(w.percent / tp * 100);
	if (projected <= 100) { return { text: fmt(S.paceOk, { p: projected }), bad: false }; }
	return { text: fmt(S.paceHigh, { p: projected }), bad: true };
}
function windowName(id) { return id === "session" ? S.windowSession : S.windowWeek; }

// ---- render ----
function chip(cls, text) { return '<div class="chip ' + cls + '"><span class="cdot"></span>' + esc(text) + "</div>"; }

function render() {
	var now = Date.now();
	var data = snapshot && snapshot.windows && snapshot.windows.length ? snapshot : lastGood;
	var root = els.root;
	root.classList.remove("blocked", "stale", "has-notice");
	els.titleText.textContent = S.title;

	if (!appReachable && !lastGood) { return blocked(S.appOffline, fmt(S.appOfflineHint, { port: currentPort() }), true, lastError); }
	if (snapshot && snapshot.state === "NoCredential") { return blocked(S.noCredential, S.noCredentialHint, false); }
	if (snapshot && snapshot.state === "Unreachable" && !data) { return blocked(S.unreachable, S.unreachableHint, false); }
	if (!data) { return blocked(S.noData, "", false); }

	var stale = !appReachable || data.isStale || (snapshot && snapshot.state !== "Ok");
	var threshold = data.thresholdPercent === undefined ? 80 : data.thresholdPercent;
	var fetched = data.fetchedAt ? new Date(data.fetchedAt) : null;
	var when = fetched ? " · " + clock(fetched) : "";
	var chips = "";
	if (!appReachable) { chips += chip("off", S.offline + when); }
	else if (stale) { chips += chip("stale", S.stale + when); }
	else { chips += chip("live", S.live + when); }
	if (data.pace) { chips += chip("pace", S.paceChip); }
	if (snapshot && snapshot.preview) { chips += chip("off", S.preview); }
	els.chips.innerHTML = chips;
	if (stale) { root.classList.add("stale"); }

	renderSessions(data.sessions);
	fitHeader();

	var find = function (id) { for (var i = 0; i < data.windows.length; i++) { if (data.windows[i].id === id) { return data.windows[i]; } } return null; };
	var session = find("session"), week = find("week"), fable = find("weekFable");

	if (session) {
		var p = clamp(session.percent, 0, 100, 0), tp = timePercent(session, now);
		els.ringUsage.style.strokeDashoffset = circ(RING_R_USAGE) * (1 - p / 100);
		els.ringTime.style.strokeDashoffset = circ(RING_R_TIME) * (1 - tp / 100);
		els.ring.classList.toggle("over", p >= threshold);
		els.heroValue.innerHTML = figure(p, session.exhausted);
		els.heroLabel.textContent = S.session;
		var r = resetText(session, now);
		els.heroReset.textContent = r ? r.num : ""; els.heroResetLabel.textContent = r ? r.lbl : "";
	}

	var rows = [];
	if (week) { rows.push(rowHtml(week, S.week, S.weekSub, threshold, now)); }
	var showFable = getIcueProperty("showFable");
	if (fable && showFable !== false && showFable !== "false") { rows.push(rowHtml(fable, S.week, S.fableSub, threshold, now)); }
	els.rows.innerHTML = rows.join("");

	if (data.pace && data.pace.runsOutAt && data.pace.resetsAt) {
		var runsOut = new Date(data.pace.runsOutAt), resets = new Date(data.pace.resetsAt);
		els.noticeText.textContent = fmt(S.paceOut, { w: windowName(data.pace.windowId), t: clock(runsOut), d: hmm(resets.getTime() - runsOut.getTime()) });
		root.classList.add("has-notice");
	}

	// Last, once every size and every piece of text is final.
	fitHero();
	alignInk();
}

function renderSessions(sessions) {
	if (!sessions || typeof sessions.open !== "number") { els.sessions.style.display = "none"; return; }
	els.sessions.style.display = "";
	els.sessions.classList.toggle("idle", sessions.working === 0);
	// The wave is switched by a class and its dots are never rebuilt, so a new
	// document arriving does not restart it mid-beat.
	els.sessions.classList.toggle("working", sessions.working > 0);
	var dots = "";
	var shown = Math.min(sessions.open, 6);
	for (var i = 0; i < shown; i++) { dots += '<span class="dot' + (i < sessions.working ? " working" : "") + '"></span>'; }
	if (els.sessionDots.innerHTML !== dots) { els.sessionDots.innerHTML = dots; }
	els.sessionsCount.innerHTML = '<span class="working">' + sessions.working + "</span>/" + sessions.open;
	els.sessionsLabel.textContent = S.sessions;
}

// Keeps the countdown inside the ring. The line sits low in the circle, where
// it is narrow, and the word beside the time is as long as its language makes
// it ("left", "restantes"). When the two do not fit the chord at that height,
// the word goes and the time stays: under "5H SESSION" it still reads.
function fitHero() {
	var line = els.heroReset.parentNode;
	line.classList.remove("short");
	var ring = els.ring.getBoundingClientRect(), box = line.getBoundingClientRect();
	if (!ring.width || !box.width) { return; }
	var radius = ring.width * 0.325;   // just inside the inner arc
	var centre = ring.top + ring.height / 2;
	var reach = Math.max(Math.abs(box.top - centre), Math.abs(box.bottom - centre));
	var chord = reach >= radius ? 0 : 2 * Math.sqrt(radius * radius - reach * reach);
	if (box.width > chord) { line.classList.add("short"); }
}

// Makes the top row fit: the sessions pill gives up its label, then its
// per-session dots, until nothing runs off the edge.
function fitHeader() {
	var h = els.header;
	h.classList.remove("tight", "tighter");
	if (h.scrollWidth <= h.clientWidth) { return; }
	h.classList.add("tight");
	if (h.scrollWidth <= h.clientWidth) { return; }
	h.classList.remove("tight");
	h.classList.add("tighter");
}

function rowHtml(w, label, sub, threshold, now) {
	var p = clamp(w.percent, 0, 100, 0), tp = timePercent(w, now), r = resetText(w, now), pace = paceText(w, now);
	var reset = !r ? "" : r.soon ? esc(S.resetsIn) + " <b>" + esc(r.num) + "</b>" : esc(S.resetsAt) + " <b>" + esc(r.num) + "</b> · " + esc(r.lbl);
	return '<div class="row' + (p >= threshold ? " over" : "") + '">' +
		'<div class="row-top"><span class="row-label">' + esc(label) + ' <span class="sub">' + esc(sub) + "</span></span>" +
		'<div class="row-value figure">' + figure(p, w.exhausted) + "</div></div>" +
		'<div class="bars"><div class="bar usage"><i style="width:' + p + '%"></i></div><div class="bar time"><i style="width:' + tp + '%"></i></div></div>' +
		'<div class="row-bottom"><span class="row-reset">' + reset + "</span>" +
		'<span class="row-pace' + (pace.bad ? " bad" : "") + '">' + esc(pace.text) + "</span></div></div>";
}

// A percentage as a number and a sign, or the cross of a spent window.
function figure(percent, exhausted) {
	return exhausted ? '<span class="num">✕</span>' : '<span class="num">' + Math.round(percent) + '</span><span class="pct">%</span>';
}

// ---- alignment by ink ----
//
// A line of text is a box, and where the letters sit inside it depends on the
// face: its ascent, its descent, how the browser rounds them at that size, and
// how tall its digits are beside its capitals. Centring boxes therefore does
// not centre letters, and two sizes side by side show it. `text-box-trim` is
// the CSS answer and iCUE's browser (Chrome 130) predates it.
//
// So the widget measures instead of assuming. The baseline comes from the page
// as laid out - an empty inline-block dropped into the element sits exactly on
// it - and the height of the ink from the element's own characters, accents and
// punctuation left out. Nothing here knows any font's metrics.
var inkCanvas = null;

// Everything below is done in whole device pixels. Glyphs are drawn on the
// pixel grid: a line moved by 0.9 px is drawn moved by 1 or by 0, and which of
// the two is not ours to choose. Measured on the XENEON EDGE from a capture:
// fractions left the label a pixel above the digits it was "exactly" level
// with. Snapping first makes what is computed and what is drawn the same thing.
function snap(v) { var d = window.devicePixelRatio || 1; return Math.round(v * d) / d; }

function inkOf(el) {
	if (!inkCanvas) { inkCanvas = document.createElement("canvas").getContext("2d"); }
	var cs = getComputedStyle(el);
	var mark = document.createElement("span");
	mark.style.cssText = "display:inline-block;width:0;height:0;vertical-align:baseline;";
	el.appendChild(mark);
	var baseline = snap(mark.getBoundingClientRect().bottom);
	el.removeChild(mark);
	inkCanvas.font = cs.fontStyle + " " + cs.fontWeight + " " + cs.fontSize + " " + cs.fontFamily;
	var text = (el.textContent || "").normalize("NFD").replace(/[^0-9A-Za-z%✕]/g, "");
	if (cs.textTransform === "uppercase") { text = text.toUpperCase(); }
	var m = inkCanvas.measureText(text || "H");
	return { top: baseline - snap(m.actualBoundingBoxAscent), bottom: baseline + snap(m.actualBoundingBoxDescent) };
}

function moveBy(el, delta) {
	delta = snap(delta);
	el.style.transform = delta === 0 ? "none" : "translateY(" + delta + "px)";
}

// Moves an element so that its ink is centred on a line, to the pixel.
function centreInk(el, centre) {
	if (!el || !el.offsetParent) { return null; }
	el.style.transform = "none";
	var ink = inkOf(el), height = ink.bottom - ink.top;
	var top = snap(centre - height / 2);
	moveBy(el, top - ink.top);
	return { top: top, bottom: top + height };
}

// Hangs a percent sign from the top of the digits beside it.
function hangSign(figureEl) {
	var num = figureEl.querySelector(".num"), sign = figureEl.querySelector(".pct");
	if (!num || !sign || !figureEl.offsetParent) { return; }
	sign.style.transform = "none";
	moveBy(sign, inkOf(num).top - inkOf(sign).top);
}

// The session bars are as tall as the digits beside them and stand on the same
// two lines: the count's ink decides, the bars copy it.
function matchBars(ink) {
	var bars = els.sessionDots;
	if (!ink || !bars.offsetParent) { return; }
	bars.style.setProperty("--bar-height", (ink.bottom - ink.top) + "px");
	bars.style.transform = "none";
	// Not snapped: a box is not a glyph, and moving it by the fraction is what
	// lands its edges on the pixel lines the digits stand on.
	var delta = ink.top - bars.getBoundingClientRect().top;
	bars.style.transform = Math.abs(delta) < 0.01 ? "none" : "translateY(" + delta.toFixed(3) + "px)";
}

function middleOf(el) { var b = el.getBoundingClientRect(); return (b.top + b.bottom) / 2; }

function alignInk() {
	try {
		centreInk(els.titleText, middleOf(els.titleText.parentNode));
		if (els.sessions.offsetParent) {
			var centre = middleOf(els.sessions);
			var count = centreInk(els.sessionsCount, centre);
			centreInk(els.sessionsLabel, centre);
			matchBars(count);
			// The wave sits on the line the text ended up on, which after snapping
			// is up to half a pixel from the pill's own middle.
			var wave = document.getElementById("thinking");
			if (count && wave && wave.offsetParent) {
				wave.style.transform = "none";
				var lift = (count.top + count.bottom) / 2 - middleOf(wave);
				wave.style.transform = Math.abs(lift) < 0.01 ? "none" : "translateY(" + lift.toFixed(3) + "px)";
			}
		}
		var figures = document.querySelectorAll(".figure");
		for (var i = 0; i < figures.length; i++) { hangSign(figures[i]); }
	} catch (e) { /* the text stays where the boxes put it */ }
}

// A face arriving changes every width and height, so the fitting is redone.
function refit() { try { render(); } catch (e) { /* nothing drawn yet */ } }
try {
	document.fonts.ready.then(refit);
	document.fonts.addEventListener("loadingdone", refit);
} catch (e) { /* no font loading API */ }
window.addEventListener("resize", refit);

function blocked(big, small, offline, diag) {
	els.stateBig.textContent = big; els.stateSmall.textContent = small;
	els.stateDiag.textContent = diag || "";
	els.retryButton.textContent = S.retry; els.reloadButton.textContent = S.reload; els.downloadButton.textContent = S.download;
	els.downloadButton.style.display = offline ? "" : "none";
	els.root.classList.add("blocked");
	els.sessions.style.display = "none";
	alignInk();
}

// ---- data: a WebSocket to ClaudeStatus, reconnecting while it is away ----
var socket = null;
var socketPort = 0;
var reconnectTimer = null;
var reconnectDelay = 2000;
var RECONNECT_MAX = 15000;

function currentPort() { return clamp(getIcueProperty("endpointPort"), 1024, 65535, DEFAULT_PORT); }

function accept(doc) {
	snapshot = doc; appReachable = true; lastError = "";
	if (doc && doc.windows && doc.windows.length) { lastGood = doc; }
	render();
}

var attempts = 0;          // connection attempts since the last document, for the screen
var insideIcue = false;    // set once iCUE has initialised the widget

// The ways to spell "this machine", in the order they are tried.
//
// Measured on iCUE 5.51 (QtWebEngine 6.9.3), 2026-09-27: a widget page iCUE has
// initialised properly is refused every connection to "localhost" and
// "127.0.0.1", WebSocket and HTTP alike, whatever its manifest declares and the
// user approves - and is let through to the IPv6 loopback literal from the
// first second. A page imported while iCUE is running is the other way round
// about "localhost". So each spelling gets its turn, the one that last opened
// goes first next time, and ClaudeStatus listens on both address families.
var HOSTS = ["[::1]", "localhost.", "localhost", "127.0.0.1"];
var hostIndex = 0;
var failedInARow = 0;      // spellings that have failed since one last opened
var HOST_RETRY_MS = 250;   // between spellings; the backoff is for a whole round

function connect() {
	if (mock) { accept(mockSnapshot(mock)); if (mock === "appOffline") { appReachable = false; render(); } return; }
	clearTimeout(reconnectTimer); reconnectTimer = null;
	if (socket) { try { socket.onclose = null; socket.close(); } catch (e) { /* already gone */ } socket = null; }
	socketPort = currentPort();
	attempts++;
	var url = "ws://" + HOSTS[hostIndex] + ":" + socketPort + "/v1/usage";
	var ws;
	try {
		ws = new WebSocket(url);
	} catch (e) {
		lost("ws: " + (e && e.message ? e.message : String(e)), url);
		return;
	}
	socket = ws;
	var opened = false;
	ws.onmessage = function (ev) {
		try { accept(JSON.parse(ev.data)); reconnectDelay = 2000; attempts = 0; } catch (e) { lastError = "bad document: " + e.message; }
	};
	ws.onopen = function () { opened = true; failedInARow = 0; lastError = ""; stopHttpPolling(); say("socket open " + url); flushSays(); };
	ws.onerror = function () { /* onclose follows with the code */ };
	ws.onclose = function (ev) {
		if (socket !== ws) { return; }
		socket = null;
		// A spelling that opened keeps its place: it is ClaudeStatus that went away.
		if (!opened) { hostIndex = (hostIndex + 1) % HOSTS.length; failedInARow++; }
		lost("ws closed " + ev.code, url);
	};
}

// The connection is gone or never came up. The retry is scheduled before
// anything else is done: the widget usually starts before ClaudeStatus does,
// and a redraw that threw here once left it on "not running" for the day.
function lost(why, url) {
	var roundOver = failedInARow === 0 || failedInARow >= HOSTS.length;
	if (roundOver) { failedInARow = 0; scheduleReconnect(); } else { retrySoon(); }
	if (attempts <= HOSTS.length * 2) { say("lost: " + why + " " + (url || "")); }
	lastError = why + " · attempt " + attempts + " · " + clock(new Date());
	if (!roundOver) { return; }
	if (attempts <= HOSTS.length * 2) { try { probe("round failed"); } catch (e) { /* a diagnostic must never stop a retry */ } }
	// While HTTP is delivering, the app is reachable; only the socket is not.
	if (!httpTimer) {
		appReachable = false;
		try { render(); } catch (e) { /* the next tick redraws */ }
	}
	try { fallbackFetch(); } catch (e) { /* the socket's verdict stands */ }
}

function retrySoon() {
	clearTimeout(reconnectTimer);
	reconnectTimer = setTimeout(connect, HOST_RETRY_MS);
}

function scheduleReconnect() {
	clearTimeout(reconnectTimer);
	reconnectTimer = setTimeout(connect, reconnectDelay);
	reconnectDelay = Math.min(RECONNECT_MAX, reconnectDelay * 2);
}

// Belt and braces: whatever happened to the timer above, a widget with no
// socket open or on its way tries again. Costs one comparison every few seconds.
setInterval(function () {
	if (mock) { return; }
	var alive = socket && (socket.readyState === 0 || socket.readyState === 1);
	if (!alive && !reconnectTimer) { reconnectDelay = 2000; connect(); }
}, 5000);

// The second way in: plain HTTP, tried once after every failed round of socket
// spellings. iCUE has refused it in every state measured so far; a browser on
// the desk, and any future iCUE that honours the manifest's permission, do not.
// While only HTTP answers, the widget polls; the moment a socket opens, the
// polling stops.
var httpTimer = null;
var HTTP_POLL_MS = 15000;
function fallbackFetch() {
	if (typeof fetch !== "function") { return; }
	var quiet = attempts > HOSTS.length * 2;
	fetch("http://localhost:" + socketPort + "/v1/usage", { cache: "no-store" })
		.then(function (r) { if (!r.ok) { throw new Error("HTTP " + r.status); } return r.json(); })
		.then(function (doc) {
			if (!quiet) { say("http ok"); }
			accept(doc);
			if (!httpTimer) { httpTimer = setInterval(function () { if (!socket || socket.readyState !== 1) { fallbackFetch(); } }, HTTP_POLL_MS); }
		})
		.catch(function (e) { if (!quiet) { say("http failed: " + (e && e.message ? e.message : String(e))); } });
}
function stopHttpPolling() { clearInterval(httpTimer); httpTimer = null; }

// A fresh attempt straight away, with the old error cleared so the screen says
// what the new attempt found rather than what the last one did.
function retryNow() {
	lastError = ""; reconnectDelay = 2000; failedInARow = 0;
	els.stateDiag.textContent = S.retrying;
	connect();
}

// The colours the widget has when iCUE hands it none: Corsair's own defaults.
var DEFAULT_STYLE = { text: "#ffffff", accent: "#ff8900", background: "#000000", transparency: 80 };

// The values are used as iCUE gives them. With "Custom Style" off, iCUE puts the
// device's default scheme into these same variables, so the widget must not
// second-guess it: an earlier build read the flag and forced its own defaults,
// which replaced the device's colours with ours.
function applyStyles() {
	var r = document.documentElement.style;
	var tc = getIcueProperty("textColor"), ac = getIcueProperty("accentColor"), bg = getIcueProperty("backgroundColor");
	r.setProperty("--text-color", typeof tc === "string" ? tc : DEFAULT_STYLE.text);
	r.setProperty("--accent-color", typeof ac === "string" ? ac : DEFAULT_STYLE.accent);
	r.setProperty("--background-color", typeof bg === "string" ? bg : DEFAULT_STYLE.background);
	r.setProperty("--widget-opacity", clamp(getIcueProperty("transparency"), 0, 100, DEFAULT_STYLE.transparency) / 100);
}

// ---- what iCUE has done to this page, for the log ----
var calls = {};
function noteCall(name) { calls[name] = (calls[name] || 0) + 1; }

// One line saying what state the widget is in. Written when a whole round of
// connection attempts has failed, which is the moment someone will want it.
function probe(why) {
	var preview = "n/a";
	try { preview = typeof iCUE === "undefined" ? "no iCUE" : String(iCUE.isPreview); } catch (e) { preview = "error " + e.message; }
	say("state " + JSON.stringify({
		why: why,
		calls: calls,
		initialised: typeof iCUE_initialized === "undefined" ? "undefined" : String(iCUE_initialized),
		preview: preview,
		attempts: attempts,
		lastError: lastError,
		port: describe("endpointPort"),
		text: describe("textColor"), accent: describe("accentColor")
	}));
}

// ---- a console for the device: a few lines to iCUE's own log through the
// console, and to ClaudeStatus's log over the socket once there is one ----
var pendingSays = [];
function describe(name) {
	var v = getIcueProperty(name);
	return v === undefined ? "undefined" : typeof v + ":" + String(v);
}
function say(text) {
	try { console.warn("ClaudeStatus widget: " + text); } catch (e) { /* no console */ }
	if (pendingSays.length && pendingSays[pendingSays.length - 1] === text) { return; }
	pendingSays.push(text);
	if (pendingSays.length > 10) { pendingSays.shift(); }
	flushSays();
}
function flushSays() {
	while (socket && socket.readyState === 1 && pendingSays.length) {
		socket.send("log:" + pendingSays.shift().slice(0, 900));
	}
}

function onIcueDataUpdated() {
	applyStyles();
	clearInterval(tickTimer);
	tickTimer = setInterval(render, 30000); // countdowns and the time arcs move between documents
	if (!socket || socketPort !== currentPort()) { connect(); } else { render(); }
}
async function onIcueInitialized() {
	insideIcue = true;
	// Strictly true: anything else iCUE might hand back here (an object, a
	// pending value) must not put sample data on a real screen.
	try { if (!mock && typeof iCUE !== "undefined" && iCUE && iCUE.isPreview === true) { mock = "preview"; } } catch (e) { /* no preview flag */ }
	// Connect first, translate second: a translation lookup that is slow or
	// fails must not stand between the widget and its data.
	onIcueDataUpdated();
	try { await loadStrings(); render(); } catch (e) { /* English stays */ }
}

// ---- tap: open the details window on the PC, or the download page when there is nothing to open ----
function openLink(url) {
	if (window.plugins && window.plugins.Linkprovider && typeof pluginLinkprovider_initialized !== "undefined" && pluginLinkprovider_initialized) {
		window.plugins.Linkprovider.open(url);
	} else {
		window.open(url, "_blank");
	}
}
function requestOpen() {
	if (socket && socket.readyState === 1) { socket.send("open"); return; }
	if (typeof fetch === "function") {
		fetch("http://localhost:" + currentPort() + "/v1/open", { method: "POST", cache: "no-store" }).catch(function () { /* nothing answering */ });
	}
}
els.retryButton.addEventListener("click", function (e) { e.stopPropagation(); retryNow(); });
els.reloadButton.addEventListener("click", function (e) { e.stopPropagation(); location.reload(); });
els.downloadButton.addEventListener("click", function (e) { e.stopPropagation(); openLink(DOWNLOAD_URL); });
document.body.addEventListener("click", function () {
	if (mock === "preview") { return; }
	// With nothing answering, a tap anywhere is a retry; the download link is its own button.
	if (!appReachable && !lastGood) { retryNow(); return; }
	requestOpen();
});

// ---- sample data ----
function mockSnapshot(kind) {
	var now = Date.now(), h = 3600000;
	var base = {
		schema: 1, state: "Ok", appVersion: "sample", fetchedAt: new Date(now - 4 * 60000).toISOString(), isStale: false, thresholdPercent: 80,
		windows: [
			{ id: "session", percent: 56, resetsAt: new Date(now + 2 * h + 11 * 60000).toISOString(), spanSeconds: 5 * 3600, exhausted: false },
			{ id: "week", percent: 18, resetsAt: new Date(now + 3 * 24 * h + 4 * h).toISOString(), spanSeconds: 7 * 86400, exhausted: false },
			{ id: "weekFable", percent: 41, resetsAt: new Date(now + 3 * 24 * h + 4 * h).toISOString(), spanSeconds: 7 * 86400, exhausted: false }
		],
		pace: null, sessions: { working: 1, open: 3 }
	};
	switch (kind) {
		case "preview": return Object.assign({}, base, { preview: true });
		case "stale": return Object.assign({}, base, { isStale: true, fetchedAt: new Date(now - 52 * 60000).toISOString() });
		case "pace":
			base.windows[0].percent = 86; base.windows[0].resetsAt = new Date(now + 1.4 * h).toISOString();
			base.pace = { windowId: "session", percentPerHour: 38, runsOutAt: new Date(now + 0.6 * h).toISOString(), resetsAt: new Date(now + 1.4 * h).toISOString() };
			base.sessions = { working: 3, open: 4 };
			return base;
		case "exhausted": base.windows[0].percent = 100; base.windows[0].exhausted = true; base.windows[0].resetsAt = new Date(now + 1.2 * h).toISOString(); return base;
		case "hot": base.windows[0].percent = 91; base.windows[1].percent = 83; base.windows[2].percent = 97; base.sessions = { working: 2, open: 2 }; return base;
		case "idle": base.sessions = { working: 0, open: 2 }; return base;
		case "noSessions": base.sessions = null; return base;
		case "noCredential": return { schema: 1, state: "NoCredential", fetchedAt: new Date(now).toISOString(), windows: [] };
		case "unreachable": return { schema: 1, state: "Unreachable", fetchedAt: new Date(now).toISOString(), windows: [] };
		case "noData": return { schema: 1, state: "NoData", windows: [] };
		case "appOffline": return null;
		default: return base;
	}
}

// ---- boot: iCUE may inject its globals a beat after this script runs, so a
// single false read is a race, not proof of a plain browser ----
var bootAttempts = 0;
function bootCheck() {
	if (typeof iCUE_initialized !== "undefined" && iCUE_initialized) { onIcueInitialized(); return; }
	if (bootAttempts++ < 15) { setTimeout(bootCheck, 100); return; }
	onIcueDataUpdated();
}

// Once per load, where the ink of the top row ended up, for the log: the one
// thing about this widget that cannot be checked anywhere but on the device.
setTimeout(function () {
	try {
		if (mock || !els.sessions.offsetParent) { return; }
		var pill = els.sessions.getBoundingClientRect();
		var r1 = function (v) { return Math.round(v * 10) / 10; };
		var span = function (el) { var i = inkOf(el); return [r1(i.top), r1(i.bottom), r1((i.top + i.bottom) / 2)]; };
		var bars = els.sessionDots.getBoundingClientRect();
		say("ink " + JSON.stringify({ dpr: window.devicePixelRatio, pillCentre: r1((pill.top + pill.bottom) / 2), bars: [r1(bars.top), r1(bars.bottom)], count: span(els.sessionsCount), label: span(els.sessionsLabel), title: span(els.titleText) }));
	} catch (e) { /* diagnostic only */ }
}, 5000);
