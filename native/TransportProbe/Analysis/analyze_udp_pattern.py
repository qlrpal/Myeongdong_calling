"""Validate exact size-order and due-time replay, then compare same-host OS boundaries."""
import hashlib
import json
import sys
from collections import Counter
from pathlib import Path
from analyze_pipeline import stats
from analyze_udp_call import load, compare, read

ORDER=('uniform1','replay1','replay2','uniform2')


def validate(app,plan,plan_bytes,mode):
    if app.get('pattern')!=mode or app.get('planHash','').lower()!=hashlib.sha256(plan_bytes).hexdigest():
        raise ValueError('Pattern mode/source hash mismatch')
    sent=app['sends'];items=plan['items']
    if len(sent)!=len(items) or [s['Sequence'] for s in sent]!=list(range(len(items))):
        raise ValueError('Incomplete send sequence')
    if [s['Bytes'] for s in sent]!=[i['Bytes'] for i in items]:
        raise ValueError('Changed packet size order')
    intended=[i['At'] for i in items]
    if mode=='uniform':
        intended=[i*intended[-1]/(len(items)-1) for i in range(len(items))]
    epoch=sent[0]['Due']-intended[0]
    planned_error=max(abs(s['Due']-epoch-t) for s,t in zip(sent,intended))*1000
    if planned_error>.001:
        raise ValueError('Changed scheduled timing')
    lateness=[(s['Start']-s['Due'])*1000 for s in sent]
    if min(lateness)<-.001:
        raise ValueError('Send before planned due time')
    observed=stats(lateness)
    return dict(mode=mode,packets=len(sent),planned_time_error_max_ms=planned_error,
        due_to_actual_start_ms=observed,starts_over_2ms_late=sum(t>2 for t in lateness),
        starts_over_10ms_late=sum(t>10 for t in lateness),p95_within_2ms=observed['p95']<=2,
        note='2ms P95 is a declared fidelity check, not a network-health threshold. Report rare larger pacing errors separately.')


def run(pc_root,laptop_root,pc_plan,laptop_plan,output):
    roots=dict(pc=Path(pc_root),laptop=Path(laptop_root));plans={}
    for role,path in (('pc',pc_plan),('laptop',laptop_plan)):
        data=Path(path).read_bytes();plan=json.loads(data.decode('utf-8-sig'));plans[role]=(plan,data)
        meta=read(roots[role]/'run.json')
        if not meta['complete'] or meta['role']!=role or [p['name'] for p in meta['phases']]!=list(ORDER) or plan['role']!=role:
            raise ValueError('Incomplete or misordered role/pattern run')
    phases=[]
    for name in ORDER:
        hosts={role:load(root,name) for role,root in roots.items()}
        summaries={}
        mode='uniform' if name.startswith('uniform') else 'replay'
        for role,h in hosts.items():
            if h['app']['role']!=role:
                raise ValueError('Test role does not match host')
            plan,data=plans[role];check=validate(h['app'],plan,data,mode)
            expected=Counter(i['Bytes'] for i in plan['items'])
            if Counter(h['size_histogram'])!=expected:
                raise ValueError('OS size histogram differs from intended packet multiset')
            summaries[role]=dict(replay_check=check,send_intervals_ms=h['send_intervals_ms'],
                control_seconds=h['control_seconds'],controls=h['controls'],metadata=h['metadata'])
        directions=[compare(hosts['pc'],hosts['laptop'],False),compare(hosts['laptop'],hosts['pc'],False)]
        phases.append(dict(name=name,hosts=summaries,directions=directions))
        print(name,[(d['direction'],d['gap_count'],d['categories']) for d in directions])
        for role,h in summaries.items():
            print(role,'fidelity',h['replay_check'],'gateway',h['controls'][1])
    result=dict(note='Equal packet count/size order within each role, changing intended timing only. No audio/WebRTC. Counterbalanced but not randomized. Original cross-host flow phase/packet content/socket options/radio state are not reproduced. OS events are not wire timestamps. No cross-host absolute time subtraction.',phases=phases)
    Path(output).write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')


def self_test():
    plan=dict(items=[dict(At=.01,Bytes=64),dict(At=.03,Bytes=80),dict(At=.05,Bytes=64)])
    data=b'test';digest=hashlib.sha256(data).hexdigest()
    for mode,dates in (('replay',[.01,.03,.05]),('uniform',[0,.025,.05])):
        app=dict(pattern=mode,planHash=digest,sends=[dict(Sequence=i,Bytes=p['Bytes'],Due=100+t,Start=100+t+.0005) for i,(p,t) in enumerate(zip(plan['items'],dates))])
        result=validate(app,plan,data,mode)
        assert result['p95_within_2ms'] and result['starts_over_2ms_late']==0
        app['sends'][1]['Bytes']=65
        try:
            validate(app,plan,data,mode);raise AssertionError('Changed size must fail')
        except ValueError:pass
        app['sends'][1]['Bytes']=plan['items'][1]['Bytes']
        app['sends'][1]['Due']+=.005
        try:
            validate(app,plan,data,mode);raise AssertionError('Changed due time must fail')
        except ValueError:pass
        app['planHash']='wrong-source'
        try:
            validate(app,plan,data,mode);raise AssertionError('Wrong source must fail')
        except ValueError:pass
    print('PASS: replay/uniform planned times, size-order integrity, source hash and measured fidelity')


if __name__=='__main__':
    if sys.argv[1:]==['--self-test']:self_test()
    elif len(sys.argv)==6:run(*sys.argv[1:])
    else:raise SystemExit('Usage: analyze_udp_pattern.py PC_ROOT LAPTOP_ROOT PC_PLAN LAPTOP_PLAN OUTPUT')
