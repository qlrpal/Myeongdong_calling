import json,pathlib,math,argparse
parser=argparse.ArgumentParser(description='Summarize same-host pipeline intervals; never estimates mouth-to-ear latency.')
parser.add_argument('directory', type=pathlib.Path, help='Directory containing *-call.json pipeline traces')
parser.add_argument('output', type=pathlib.Path, help='New summary JSON path (must not overwrite an input)')
args=parser.parse_args()
root=args.directory
inputs=sorted(root.glob('*-call.json'))
if not inputs: parser.error('No *-call.json traces found')
if args.output.resolve() in [p.resolve() for p in inputs]: parser.error('Output must not overwrite an input')
def stat(v):
 v=sorted(v);return {k:round(v[min(len(v)-1,math.ceil(len(v)*q)-1)],3) for k,q in [('p50',.5),('p95',.95),('p99',.99)]} if v else None
out=[]
for p in inputs:
 d=json.loads(p.read_text(encoding='utf-8-sig'));
 if d['error'] is not None or d['trace']['EvictedRecords']: raise ValueError(f'Incomplete trace: {p}')
 t=d['trace'];ss=t['Sends'];lo=ss[0]['SendEnd']+5;hi=ss[-1]['SendEnd']-1;s=[x for x in ss if lo<x['SendEnd']<hi and x['Capture'] and x['Capture']['FirstSampleAt']];st={}
 pairs={'sampleToFirstCallback':('FirstSampleAt','FirstCallbackAt'),'assemblyAfterFirstCallback':('FirstCallbackAt','FrameReadyAt'),'readyToEncode':('FrameReadyAt','EncodeStart'),'encode':('EncodeStart','EncodeEnd')}
 for name,(a,b) in pairs.items():st[name]=stat([(x['Capture'][b]-x['Capture'][a])*1000 for x in s])
 st['sampleToFrameReady']=stat([(x['Capture']['FrameReadyAt']-x['Capture']['FirstSampleAt'])*1000 for x in s]);st['sampleToSendEnd']=stat([(x['SendEnd']-x['Capture']['FirstSampleAt'])*1000 for x in s]);st['encodedToSendEnd']=stat([(x['SendEnd']-x['Capture']['EncodeEnd'])*1000 for x in s]);st['sendQueue']=stat([(x['DequeuedAt']-x['QueuedAt'])*1000 for x in s]);st['sendApi']=stat([(x['SendEnd']-x['SendStart'])*1000 for x in s])
 rt=t['Receives'];low=rt[0]['At']+5;high=rt[-1]['At']-1;rx={(x['Ssrc'],x['Timestamp']):x for x in rt if low<x['At']<high};play=[x for x in t['Playout'] if x['Outcome']=='played' and (x['Ssrc'],x['Timestamp']) in rx]
 st['receiveToSubmit']=stat([(x['SubmittedAt']-rx[(x['Ssrc'],x['Timestamp'])]['At'])*1000 for x in play]);st['decode']=stat([(x['DecodeEnd']-x['DecodeStart'])*1000 for x in play]);rr=[x for x in t['Reads'] if low<x['CalledAt']<high];st['renderRequestMs']=stat([x['RequestedSamples']/48 for x in rr]);st['renderReadWork']=stat([(x['ReturnedAt']-x['LockAt'])*1000 for x in rr]);st['renderLockWait']=stat([(x['LockAt']-x['CalledAt'])*1000 for x in rr]);samples=d['samples'][5:-1];st['outputApiReported']=stat([x['Peers'][0]['Audio']['DeviceLatencyMs'] for x in samples if x['Peers'][0]['Audio']['DeviceLatencyMs'] is not None]);st['queueAfterRead']=stat([x['QueueAfterMs'] for x in rr]);out.append(dict(file=p.name,encoder=d['samples'][-1]['Encoder'],stages=st))
args.output.parent.mkdir(parents=True,exist_ok=True)
args.output.write_text(json.dumps(out,indent=2,ensure_ascii=False),encoding='utf-8')
print(f'Summarized {len(out)} traces: {args.output}')
