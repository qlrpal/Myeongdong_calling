"""Patch only the pinned 10.0.17 source, for isolated diagnostic builds.

No NuGet cache or product binaries are modified. See README.md in this directory.
"""
import hashlib
from pathlib import Path
import sys

EXPECTED = '335c8aa5934a9bf0d24a1df27ef8ef9e2c9d5b9f4f6b237099bddc407227e8fc'

def patch(path):
    data = path.read_bytes()
    if hashlib.sha256(data).hexdigest() != EXPECTED:
        raise SystemExit('Refusing unknown/already patched UdpReceiver.cs')
    source = data.decode('utf-8-sig').replace('\r\n', '\n')
    def replace(old, new):
        nonlocal source
        if source.count(old) != 1:
            raise SystemExit('Patch anchor count mismatch: ' + old[:70])
        source = source.replace(old, new)
    replace('    protected byte[] m_recvBuffer;', '''    protected byte[] m_recvBuffer;
    private readonly object _receiveBeginSync = new object();
    private readonly bool _serializeReceiveBegin =
        Environment.GetEnvironmentVariable("VOICE_SDK_RECEIVE_GATE") == "serialized";
    private readonly UdpReceiveExperiment.State _receiveExperiment;''')
    replace('        m_recvBuffer = new byte[mtu];', '''        m_recvBuffer = new byte[mtu];
        _receiveExperiment = UdpReceiveExperiment.Create(m_localEndPoint, m_recvBuffer);''')
    replace('    public virtual void BeginReceiveFrom()\n    {', '''    public virtual void BeginReceiveFrom()
    {
        // Diagnostic A/B switch. The stock NuGet binary is unaffected.
        if (_serializeReceiveBegin)
        {
            // Only claim the receive slot under the lock. APM callbacks may run
            // inline, so never hold this lock across posting or packet handlers.
            lock (_receiveBeginSync)
            {
                if (m_isClosed && m_isRunningReceive) m_isRunningReceive = false;
                if (m_isRunningReceive || m_isClosed) return;
                m_isRunningReceive = true;
            }
        }
        BeginReceiveFromCore(_serializeReceiveBegin);
    }

    private void BeginReceiveFromCore(bool claimed)
    {''')
    replace('''        //Prevent call BeginReceiveFrom if it is already running
        if (m_isClosed && m_isRunningReceive)
        {
            m_isRunningReceive = false;
        }
        if (m_isRunningReceive || m_isClosed)
        {
            return;
        }''', '''        if (!claimed)
        {
            // Original unsynchronised check/set for the A control condition.
            if (m_isClosed && m_isRunningReceive) m_isRunningReceive = false;
            if (m_isRunningReceive || m_isClosed) return;
        }''')
    replace('''        try
        {
            m_isRunningReceive = true;''', '''        UdpReceiveExperiment.Operation operation = null;
        try
        {
            m_isRunningReceive = true;
            operation = _receiveExperiment?.Begin();''')
    replace('ref recvEndPoint, EndReceiveFrom, null);', 'ref recvEndPoint, EndReceiveFrom, operation);')
    for catch in ['catch (ObjectDisposedException)', 'catch (SocketException sockExcp)', 'catch (Exception excp)']:
        # These exact indents occur once in Begin; End has different catch text/comments.
        old = '        ' + catch + '\n        {\n'
        pos = source.index(old, source.index('private void BeginReceiveFromCore'))
        source = source[:pos] + source[pos:].replace(old, old + '            operation?.Finish();\n', 1)
    replace('''    protected virtual void EndReceiveFrom(IAsyncResult ar)
    {
        try''', '''    protected virtual void EndReceiveFrom(IAsyncResult ar)
    {
        var operation = ar.AsyncState as UdpReceiveExperiment.Operation;
        try''')
    replace('                int bytesRead = m_socket.EndReceiveFrom(ar, ref remoteEP);', '''                int bytesRead = m_socket.EndReceiveFrom(ar, ref remoteEP);
                operation?.CompleteIo();''')
    replace('''        finally
        {
            m_isRunningReceive = false;''', '''        finally
        {
            // End ownership before clearing the SDK gate and rearming.
            operation?.Finish();
            m_isRunningReceive = false;''')
    path.write_text(source, encoding='utf-8', newline='\n')
    helper = Path(__file__).with_name('UdpReceiveExperiment.cs')
    path.with_name(helper.name).write_bytes(helper.read_bytes())
    print('Patched pinned UdpReceiver; diagnostic gate selected per process environment.')

if __name__ == '__main__':
    if len(sys.argv) != 2:
        raise SystemExit('Usage: patch_udp_receiver.py PINNED_SOURCE/UdpReceiver.cs')
    patch(Path(sys.argv[1]))
