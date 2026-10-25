"""Pinned, diagnostic-only UDP TURN allocation release before the socket closes."""
import hashlib
from pathlib import Path
import sys

EXPECTED = 'd967aea0c5c82b7baf96e20dc082ca5287f22c60b4dbe94b4824801c75fe50a9'

def patch(path):
    data = path.read_bytes()
    if hashlib.sha256(data).hexdigest() != EXPECTED:
        raise SystemExit('Refusing unknown/already patched RtpIceChannel.cs')
    source = data.decode('utf-8-sig').replace('\r\n', '\n')
    old = '''                _closed = true;
                _connectivityChecksTimer?.Dispose();
                _processIceServersTimer?.Dispose();
                _refreshTurnTimer?.Dispose();'''
    if source.count(old) != 1:
        raise SystemExit('TURN close patch anchor mismatch')
    source = source.replace(old, old + '\n                ReleaseUdpTurnAllocations();')
    anchor = '        public void AddRemoteCandidate(RTCIceCandidate candidate)'
    method = '''        private void ReleaseUdpTurnAllocations()
        {
            // Test A/B switch. RFC 8656 section 8: delete using authenticated Refresh(0).
            if (Environment.GetEnvironmentVariable("VOICE_SDK_TURN_RELEASE") != "1") return;
            lock (_iceServerResolver)
            {
                foreach (var server in _iceServerResolver.IceServers.Values)
                {
                    if (server.Protocol != ProtocolType.Udp || server.RelayEndPoint == null ||
                        server.ServerEndPoint == null || server.Nonce == null || server.Realm == null ||
                        server._username == null || server._password == null) continue;
                    try
                    {
                        var request = new STUNMessage(STUNMessageTypesEnum.Refresh);
                        request.Header.TransactionId = Encoding.ASCII.GetBytes(Crypto.GetRandomString(STUNHeader.TRANSACTION_ID_LENGTH));
                        request.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.Lifetime, (uint)0));
                        var bytes = GetAuthenticatedStunRequest(request, server._username, server.Realm, server._password, server.Nonce);
                        var endpoint = server.ServerEndPoint;
                        if (endpoint.AddressFamily == AddressFamily.InterNetwork && RtpSocket.AddressFamily == AddressFamily.InterNetworkV6)
                            endpoint = new IPEndPoint(endpoint.Address.MapToIPv6(), endpoint.Port);
                        // The channel socket is still open here. Do not enqueue a send then cancel it by closing.
                        // Bound shutdown work; media on this channel has already stopped.
                        RtpSocket.SendTimeout = 100;
                        RtpSocket.SendTo(bytes, endpoint);
                        logger.LogDebug("TURN UDP allocation release requested before socket close.");
                    }
                    catch (SocketException ex)
                    { logger.LogWarning("TURN UDP release send failed: {SocketErrorCode}.", ex.SocketErrorCode); }
                    catch (ObjectDisposedException)
                    { logger.LogDebug("TURN UDP release skipped: socket already closed."); }
                }
            }
        }

'''
    if source.count(anchor) != 1:
        raise SystemExit('TURN method patch anchor mismatch')
    # Insert before the XML comment belonging to AddRemoteCandidate.
    comment = source.rfind('        /// <summary>', 0, source.index(anchor))
    source = source[:comment] + method + source[comment:]
    path.write_text(source, encoding='utf-8', newline='\n')
    print('Patched pinned TURN UDP close; optional authenticated Refresh(0), no success ACK claim.')

if __name__ == '__main__':
    if len(sys.argv) != 2:
        raise SystemExit('Usage: patch_turn_release.py PINNED_SOURCE/RtpIceChannel.cs')
    patch(Path(sys.argv[1]))
