import { createHmac, randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';

// The shared secret stays on the server. Clients receive only short-lived credentials.
export function createIceConfig(env = process.env) {
  const base = env.ICE_SERVERS ? JSON.parse(env.ICE_SERVERS) : [{ urls: 'stun:stun.l.google.com:19302' }];
  if (!Array.isArray(base)) throw new Error('ICE_SERVERS must be a JSON array.');
  const urls = (env.TURN_URLS || '').split(',').map(value => value.trim()).filter(Boolean);
  if (!urls.length) return () => ({ iceServers: base });
  if (urls.some(url => !/^turns?:[^\s]+$/.test(url))) throw new Error('TURN_URLS must contain TURN URLs.');
  const secret = env.TURN_SECRET_FILE ? readFileSync(env.TURN_SECRET_FILE, 'utf8').trim() : env.TURN_SECRET;
  if (!secret || secret.length < 32) throw new Error('TURN requires a shared secret of at least 32 characters.');
  const ttl = Number(env.TURN_TTL_SECONDS || 3600);
  if (!Number.isInteger(ttl) || ttl < 600 || ttl > 86400) throw new Error('TURN_TTL_SECONDS must be 600..86400.');
  return () => {
    const expiresAt = Math.floor(Date.now() / 1000) + ttl;
    const username = `${expiresAt}:${randomUUID()}`;
    const credential = createHmac('sha1', secret).update(username).digest('base64');
    return { iceServers: [...base, { urls, username, credential }], turnCredentialsExpireAt: expiresAt };
  };
}
