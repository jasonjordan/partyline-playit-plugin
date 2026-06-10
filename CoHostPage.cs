namespace Partyline
{
    internal static class CoHostPage
    {
        public static string GetHtml()
        {
            return @"<!DOCTYPE html>
<html>
<head>
  <meta charset='utf-8'>
  <meta name='viewport' content='width=device-width, initial-scale=1'>
  <title>Partyline Co-Host</title>
  <style>
    * { box-sizing: border-box; margin: 0; padding: 0; }
    body { font-family: -apple-system, sans-serif; background: #1a1a2e; color: white; min-height: 100vh; display: flex; align-items: center; justify-content: center; }
    .container { max-width: 500px; width: 100%; padding: 2rem; }
    h1 { font-size: 1.5rem; margin-bottom: 1.5rem; text-align: center; }
    .status { padding: 1rem; border-radius: 8px; margin-bottom: 1.5rem; text-align: center; font-weight: bold; }
    .status-disconnected { background: rgba(239,68,68,0.2); border: 1px solid #ef4444; }
    .status-connecting { background: rgba(234,179,8,0.2); border: 1px solid #eab308; }
    .status-connected { background: rgba(34,197,94,0.2); border: 1px solid #22c55e; }
    .ptt-btn { width: 100%; padding: 2rem; font-size: 1.5rem; font-weight: bold; border: none; border-radius: 12px; cursor: pointer; user-select: none; transition: all 0.1s; }
    .ptt-idle { background: #64748b; color: white; }
    .ptt-active { background: #ef4444; color: white; transform: scale(0.98); }
    .ptt-btn:disabled { opacity: 0.5; cursor: not-allowed; }
    .controls { margin-top: 1.5rem; }
    .connect-btn { width: 100%; padding: 1rem; font-size: 1.1rem; border: none; border-radius: 8px; cursor: pointer; background: #3b82f6; color: white; font-weight: bold; }
    .connect-btn:hover { background: #2563eb; }
    .vu { height: 8px; background: rgba(255,255,255,0.1); border-radius: 4px; overflow: hidden; margin-top: 1rem; }
    .vu-fill { height: 100%; background: #22c55e; width: 0%; transition: width 0.05s; }
    .info { margin-top: 1rem; font-size: 0.8rem; color: #94a3b8; text-align: center; }
  </style>
</head>
<body>
  <div class='container'>
    <h1>🎙️ Partyline Co-Host</h1>
    
    <div id='status' class='status status-disconnected'>Disconnected</div>
    
    <div class='controls'>
      <button id='connect-btn' class='connect-btn' onclick='connect()'>Connect</button>
    </div>

    <button id='ptt' class='ptt-btn ptt-idle' disabled
      onmousedown='engagePTT()' onmouseup='releasePTT()' onmouseleave='releasePTT()'
      ontouchstart='engagePTT(); event.preventDefault();' ontouchend='releasePTT(); event.preventDefault();'>
      PUSH TO TALK
    </button>

    <div class='vu'><div id='vu-fill' class='vu-fill'></div></div>
    <div class='info'>Hold the button to talk. Release to mute.</div>
  </div>

  <script>
    let pc, localStream, audioTrack;
    let sessionId;
    const basePath = window.location.pathname.replace(/\/join\/?$/, '').replace(/\/$/, '');

    async function connect() {
      document.getElementById('status').className = 'status status-connecting';
      document.getElementById('status').innerText = 'Connecting...';
      document.getElementById('connect-btn').disabled = true;

      try {
        localStream = await navigator.mediaDevices.getUserMedia({ audio: true });
        audioTrack = localStream.getAudioTracks()[0];
        audioTrack.enabled = false; // Start muted (PTT)

        pc = new RTCPeerConnection({
          iceServers: [{ urls: 'stun:stun.l.google.com:19302' }]
        });

        pc.addTrack(audioTrack, localStream);

        // Handle incoming audio (DJ mix)
        pc.ontrack = (event) => {
          const audio = new Audio();
          audio.srcObject = event.streams[0];
          audio.play();
        };

        pc.onicecandidate = (event) => {
          if (event.candidate) {
            fetch(basePath + '/api/ice', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({
                sessionId: sessionId,
                candidate: event.candidate.candidate,
                sdpMid: event.candidate.sdpMid
              })
            });
          }
        };

        pc.onconnectionstatechange = () => {
          if (pc.connectionState === 'connected') {
            document.getElementById('status').className = 'status status-connected';
            document.getElementById('status').innerText = 'Connected ✓';
            document.getElementById('ptt').disabled = false;
          } else if (pc.connectionState === 'disconnected' || pc.connectionState === 'failed') {
            document.getElementById('status').className = 'status status-disconnected';
            document.getElementById('status').innerText = 'Disconnected';
            document.getElementById('ptt').disabled = true;
          }
        };

        const offer = await pc.createOffer();
        await pc.setLocalDescription(offer);

        const res = await fetch(basePath + '/api/offer', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ sdp: offer.sdp, type: 'offer' })
        });

        const answer = await res.json();
        sessionId = answer.sessionId;
        await pc.setRemoteDescription({ type: 'answer', sdp: answer.sdp });

      } catch (err) {
        document.getElementById('status').className = 'status status-disconnected';
        document.getElementById('status').innerText = 'Error: ' + err.message;
        document.getElementById('connect-btn').disabled = false;
      }
    }

    function engagePTT() {
      if (!audioTrack) return;
      audioTrack.enabled = true;
      document.getElementById('ptt').className = 'ptt-btn ptt-active';
      document.getElementById('ptt').innerText = '🎙️ LIVE';
    }

    function releasePTT() {
      if (!audioTrack) return;
      audioTrack.enabled = false;
      document.getElementById('ptt').className = 'ptt-btn ptt-idle';
      document.getElementById('ptt').innerText = 'PUSH TO TALK';
    }

    // VU Meter
    setInterval(() => {
      if (!localStream || !audioTrack?.enabled) {
        document.getElementById('vu-fill').style.width = '0%';
        return;
      }
      // Simple VU based on track activity
      document.getElementById('vu-fill').style.width = audioTrack.enabled ? '60%' : '0%';
    }, 100);
  </script>
</body>
</html>";
        }
    }
}
