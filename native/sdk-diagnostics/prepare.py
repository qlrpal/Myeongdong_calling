"""Build a separate diagnostics-only copy of the already patched, pinned SDK."""
from pathlib import Path
import json,shutil,subprocess,uuid,hashlib,os,sys

root=Path(__file__).resolve().parents[2];native=root/'native'
manifest=json.loads((native/'.local/patched-sdk/manifest.json').read_text(encoding='utf-8-sig'))
source=Path(manifest['sourceDirectory']).resolve();cache=(native/'.local/patched-sdk').resolve()
if not source.is_relative_to(cache):raise ValueError('Unexpected source root')
digest=lambda p:hashlib.sha256(Path(p).read_bytes()).hexdigest().upper()
if digest(cache/'SIPSorcery.dll')!=manifest['sdkSha256']:raise ValueError('Baseline SDK cache changed')
for path,key in [('net/RTP/UdpReceiver.cs','receiverSha256'),('net/ICE/RtpIceChannel.cs','iceSha256'),('net/DtlsSrtp/Lib/SRTP/SrtpContext.cs','srtpSha256')]:
 if digest(source/path)!=manifest[key]:raise ValueError('Patched source changed: '+path)
trial=native/'.local'/('sdk-boundary-'+uuid.uuid4().hex);trial.mkdir();target=trial/'source'
shutil.copytree(source,target,ignore=shutil.ignore_patterns('bin','obj'))
def once(text,old,new):
 if text.count(old)!=1:raise ValueError('Pinned source anchor count mismatch: '+old[:90])
 return text.replace(old,new)
def read(path):return path.read_text(encoding='utf-8-sig').replace('\r\n','\n')
def write(path,text):path.write_text(text,encoding='utf-8',newline='\n')
p=target/'net/RTP/UdpReceiver.cs';text=read(p)
for line,var in [('int bytesRead = m_socket.EndReceiveFrom(ar, ref remoteEP);','bytesRead'),('int bytesReadSync = m_socket.ReceiveFrom(m_recvBuffer, 0, m_recvBuffer.Length, SocketFlags.None, ref remoteEP);','bytesReadSync')]:
 text=once(text,line,line+'\n                MyeongdongBoundaryProbe.Observe("udp-receive-end", m_localEndPoint.Port, m_recvBuffer.AsSpan(0, '+var+'), '+var+', 0);')
write(p,text)
p=target/'net/RTP/RTPChannel.cs';text=read(p);begin=text.index('    public virtual SocketError Send(');end=text.index('    public SocketError SendRelay(',begin)
segment=text[begin:end];segment=once(segment,'    {\n        if (m_isClosed)','    {\n        MyeongdongBoundaryProbe.SendToken boundarySend = null;\n        if (m_isClosed)')
old='sendSocket.BeginSendTo(buffer, 0, buffer.Length, SocketFlags.None, dstEndPoint, EndSendTo, sendSocket);'
new='boundarySend = MyeongdongBoundaryProbe.Sending(sendSocket, sendOn == RTPChannelSocketsEnum.Control ? ControlPort : RTPPort, buffer);\n                sendSocket.BeginSendTo(buffer, 0, buffer.Length, SocketFlags.None, dstEndPoint, EndSendTo, (object)boundarySend ?? sendSocket);'
segment=once(segment,old,new)
segment=once(segment,'return sockExcp.SocketErrorCode;','MyeongdongBoundaryProbe.Observe("socket-send-rejected", RTPPort, buffer, buffer.Length, (int)sockExcp.SocketErrorCode);\n                return sockExcp.SocketErrorCode;')
text=text[:begin]+segment+text[end:]
begin=text.index('    private void EndSendTo(');end=text.index('    /// <summary>',begin)
segment=text[begin:end];segment=once(segment,'    {\n        try','    {\n        var boundarySend = ar?.AsyncState as MyeongdongBoundaryProbe.SendToken;\n        try')
segment=once(segment,'Socket sendSocket = (Socket)ar.AsyncState;','Socket sendSocket = boundarySend != null ? boundarySend.Socket : (Socket)ar.AsyncState;')
segment=once(segment,'int bytesSent = sendSocket.EndSendTo(ar);','int bytesSent = sendSocket.EndSendTo(ar);\n            if (boundarySend != null) MyeongdongBoundaryProbe.Emit("socket-send-end-observed", boundarySend.Port, boundarySend.Header, bytesSent, 0);')
segment=once(segment,'catch (SocketException sockExcp)\n        {','catch (SocketException sockExcp)\n        {\n            if (boundarySend != null) MyeongdongBoundaryProbe.Emit("socket-send-end-error", boundarySend.Port, boundarySend.Header, 0, (int)sockExcp.SocketErrorCode);')
segment=once(segment,'catch (ObjectDisposedException) // Thrown when socket is closed. Can be safely ignored.\n        { }','catch (ObjectDisposedException) // Thrown when socket is closed. Can be safely ignored.\n        { if (boundarySend != null) MyeongdongBoundaryProbe.Emit("socket-send-end-error", boundarySend.Port, boundarySend.Header, 0, (int)SocketError.OperationAborted); }')
text=text[:begin]+segment+text[end:];write(p,text)
p=target/'net/RTP/Streams/MediaStream.cs';text=read(p)
old='res = unprotectRtpPacket(buffer, buffer.Length, out outBufLen);'
new='var boundaryHeader = MyeongdongBoundaryProbe.Read(buffer);\n                MyeongdongBoundaryProbe.Emit("srtp-unprotect-enter", rtpChannel?.RTPPort ?? 0, boundaryHeader, buffer.Length, 0);\n                '+old+'\n                MyeongdongBoundaryProbe.Emit("srtp-unprotect-return", rtpChannel?.RTPPort ?? 0, boundaryHeader, outBufLen, res);'
write(p,once(text,old,new))
project=read(native/'sdk/PatchedSipsorcery.csproj')
project=once(project,'10.0.17+myeongdong.2','10.0.17+myeongdong.2.boundary1')
project=once(project,'    <Compile Include="PatchIdentity.cs" />','    <Compile Include="PatchIdentity.cs" />\n    <Compile Include="BoundaryProbe.cs" />')
write(trial/'SdkBoundary.csproj',project)
for name in ('RuntimeDependencies.props','packages.lock.json'):shutil.copyfile(native/'sdk'/name,trial/name)
write(trial/'PatchIdentity.cs',read(native/'sdk/PatchIdentity.cs')+'\n[assembly: AssemblyMetadata("MyeongdongDiagnosticSdk", "boundary1")]\n')
shutil.copyfile(native/'sdk-diagnostics/BoundaryProbe.cs',trial/'BoundaryProbe.cs')
env=os.environ.copy();env['DOTNET_CLI_HOME']=str(native/'.dotnet');env['NUGET_PACKAGES']=str(native/'.nuget')
for args in (['restore',str(trial/'SdkBoundary.csproj'),'--locked-mode'],['build',str(trial/'SdkBoundary.csproj'),'-c','Release','--no-restore']):
 subprocess.run(['dotnet']+args+['-p:SdkSourceRoot='+str(target)],env=env,check=True)
binary=trial/'probe';shutil.copytree(native/'TransportProbe/bin/Release/net10.0-windows10.0.19041.0',binary)
shutil.copyfile(trial/'bin/Release/net10.0/SIPSorcery.dll',binary/'SIPSorcery.dll')
result={'trial':str(trial),'binary':str(binary),'baselineSdkSha256':manifest['sdkSha256'],'diagnosticSdkSha256':digest(binary/'SIPSorcery.dll'),'sourceCommit':manifest['upstreamCommit'],'diagnosticOnly':True}
(trial/'manifest.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
if len(sys.argv)==2:Path(sys.argv[1]).write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps(result),flush=True)
