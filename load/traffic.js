// CounterShift load generator (k6).
//
//   docker run --rm -i -v "<repo>/load:/load" -e BASE_URL=http://host.docker.internal:5072 grafana/k6:2.3.0 run /load/traffic.js
//
// Two open-model scenarios ("constant arrival rate"): k6 starts N iterations per second whether or not earlier
// ones have finished. A closed model ("N virtual users in a loop") slows down when the server slows down, which
// hides latency problems. Real clients don't wait politely, and neither does this.
//
// Environment:
//   BASE_URL       API root                                  (default http://host.docker.internal:5072)
//   PREFIX         prefix for card/user IDs; use a new one per run so runs never share keys
//   EVENT_RATE     events per second                         (default 200)
//   DECISION_RATE  decisions per second                      (default 400)
//   DURATION       e.g. "10m"                                (default 5m)
//   CARDS, USERS   key space sizes                           (default 5000, 2000)
//   SUMMARY        path for the JSON summary                 (default /load/results/summary-<PREFIX>.json)

import http from 'k6/http';
import { check } from 'k6';

const BASE = __ENV.BASE_URL || 'http://host.docker.internal:5072';
const PREFIX = __ENV.PREFIX || `run${Date.now()}`;
const CARDS = parseInt(__ENV.CARDS || '5000');
const USERS = parseInt(__ENV.USERS || '2000');
const DURATION = __ENV.DURATION || '5m';

export const options = {
  discardResponseBodies: true,
  scenarios: {
    events: {
      executor: 'constant-arrival-rate', exec: 'ingest',
      rate: parseInt(__ENV.EVENT_RATE || '200'), timeUnit: '1s', duration: DURATION,
      preAllocatedVUs: 50, maxVUs: 400,
    },
    decisions: {
      executor: 'constant-arrival-rate', exec: 'decide',
      rate: parseInt(__ENV.DECISION_RATE || '400'), timeUnit: '1s', duration: DURATION,
      preAllocatedVUs: 50, maxVUs: 400,
    },
  },
  // Not a pass/fail gate here: the rehearsal reports the numbers. But a non-zero failure rate is the headline.
  thresholds: { http_req_failed: ['rate<0.001'] },
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
};

// Skewed keys: 30% of traffic goes to 1% of cards (an attacker hammering a few cards); the rest is uniform.
function card() {
  const hot = Math.max(1, Math.floor(CARDS / 100));
  const i = Math.random() < 0.3 ? Math.floor(Math.random() * hot) : Math.floor(Math.random() * CARDS);
  return `${PREFIX}_c${i}`;
}
function user() { return `${PREFIX}_u${Math.floor(Math.random() * USERS)}`; }

const JSON_HEADERS = { headers: { 'Content-Type': 'application/json' } };

export function ingest() {
  const r = Math.random();
  const type = r < 0.6 ? 'payment.failed' : r < 0.85 ? 'login.failed' : 'ride.requested';
  const entities = type === 'payment.failed' ? { cardId: card(), userId: user() } : { userId: user() };
  const res = http.post(`${BASE}/v1/events`, JSON.stringify({
    eventId: crypto.randomUUID(), type, occurredAt: new Date().toISOString(), entities,
  }), { ...JSON_HEADERS, tags: { name: 'events' } });
  check(res, { 'event 200': (x) => x.status === 200 });
}

export function decide() {
  const r = Math.random();
  const [action, entities] = r < 0.6 ? ['payment', { cardId: card(), userId: user() }]
    : r < 0.85 ? ['login', { userId: user() }] : ['ride', { userId: user() }];
  const res = http.post(`${BASE}/v1/decisions`, JSON.stringify({ action, entities }), { ...JSON_HEADERS, tags: { name: 'decisions' } });
  check(res, { 'decision 200': (x) => x.status === 200 });
}

export function handleSummary(data) {
  const path = __ENV.SUMMARY || `/load/results/summary-${PREFIX}.json`;
  return { [path]: JSON.stringify(data, null, 2), stdout: `k6 done: ${data.metrics.http_reqs.values.count} requests, failed rate ${data.metrics.http_req_failed.values.rate}\n` };
}
