export const expectedChecks = ['baseline', 'muted', 'active', 'a-rejoin', 'b-rejoin', 'cleanup'];
export function summarizePair(a, b) {
  const reasons = [];
  for (const [name, data] of [['a', a], ['b', b]]) {
    if (data?.schemaVersion !== 1 || !data.completed || data.error || data.role !== name
      || !expectedChecks.every(check => data.checks?.some(c => c.name === check && c.ok === true)))
      reasons.push(name + ': 오류 또는 불완전한 검사');
    if (data?.traceBaseline === true && (data.observeSeconds > 45 || data.baselineTrace?.EvictedRecords !== 0
      || !['Reads', 'Admissions', 'Receives'].every(key => data.baselineTrace?.[key]?.length > 0)))
      reasons.push(name + ': 상세 기준선 기록 누락/축출');
    if (!/^[0-9a-f]{32}$/i.test(data?.run ?? '') || !['direct', 'relay'].includes(data?.route)
      || typeof data?.shared !== 'boolean'
      || !['buildHash', 'probeHash', 'fixtureHash', 'hostFingerprint'].every(key => /^[0-9a-f]{64}$/i.test(data?.[key] ?? '')))
      reasons.push(name + ': 식별 정보 누락');
  }
  if (['run', 'route', 'shared', 'buildHash', 'probeHash', 'fixtureHash'].some(key => a?.[key] !== b?.[key])
    || (a?.observeSeconds ?? 5) !== (b?.observeSeconds ?? 5)
    || (a?.traceBaseline ?? false) !== (b?.traceBaseline ?? false)
    || (a?.reserveMs ?? 20) !== (b?.reserveMs ?? 20)
    || JSON.stringify(a?.sdk) !== JSON.stringify(b?.sdk)) reasons.push('양쪽 실행·경로·출력·빌드·시험 파형 불일치');
  const functionalPassed = reasons.length === 0;
  const differentHostFingerprints = !!a?.hostFingerprint && !!b?.hostFingerprint && a.hostFingerprint !== b.hostFingerprint;
  if (functionalPassed && !differentHostFingerprints) reasons.push('같은 호스트 식별자: 두 PC 검증 미완료');
  return { schemaVersion: 1, status: !functionalPassed ? 'fail' : differentHostFingerprints ? 'pass' : 'incomplete',
    functionalPassed, differentHostFingerprints, reasons, run: a?.run, route: a?.route, shared: a?.shared,
    mediaAddresses: { a: a?.mediaAddress ?? null, b: b?.mediaAddress ?? null },
    notes: '호스트 식별자는 컴퓨터 이름의 해시이며 독립 하드웨어의 증명은 아니다. 기능 검사이며 물리 지연·게임 부하·연속성 품질 판정은 포함하지 않는다.' };
}
