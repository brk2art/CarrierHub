// Creates shipments through the Public API at a steady rate, spread over the three carriers.
// k6 measures the API; the rest of the journey is read from Aspire's metrics
// (gateway.round.duration, gateway.event.age, outbox.wait, webhook.delivery_time).
//
//   $env:TOKEN = "<merchant-1 token>"
//   k6 run -e RATE=2 -e DURATION=1m tests/load/shipments.js

import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';
import exec from 'k6/execution';

const BASE_URL = __ENV.BASE_URL || 'https://localhost:7120';
const TOKEN = __ENV.TOKEN;
const RATE = Number(__ENV.RATE || 2);           // new shipments per second
const DURATION = __ENV.DURATION || '1m';
// Part of every order and tracking number, so runs never collide with each other or with earlier data.
const RUN = __ENV.RUN || String(Date.now()).slice(-6);

const throttled = new Counter('throttled');     // 429s from the per-merchant rate limit

export const options = {
    insecureSkipTLSVerify: true,                // the ASP.NET development certificate on localhost
    scenarios: {
        create: {
            // A fixed number of new shipments per second, however slow the API gets.
            // (A fixed number of users would slow down with the API and hide the problem.)
            executor: 'constant-arrival-rate',
            rate: RATE,
            timeUnit: '1s',
            duration: DURATION,
            preAllocatedVUs: 20,
            maxVUs: 200,
        },
    },
    thresholds: {
        'http_req_duration{name:create}': ['p(95)<500'],
        checks: ['rate>0.99'],
    },
};

const carriers = [
    { name: 'FastShip', prefix: 'FS' },
    { name: 'NordPost', prefix: 'NP' },
    { name: 'ParcelGo', prefix: 'PG' },
];

export function setup() {
    if (!TOKEN) throw new Error('Set the merchant token first: $env:TOKEN = "<token>"');
    console.log(`Run ${RUN}: ${RATE} shipments/s for ${DURATION}`);
}

export default function () {
    const n = exec.scenario.iterationInTest;
    const carrier = carriers[n % carriers.length];

    const body = JSON.stringify({
        merchantReference: `LOAD-${RUN}-${n}`,
        carrier: carrier.name,
        trackingNumber: `${carrier.prefix}${RUN}${String(n).padStart(6, '0')}`,
        promisedBy: new Date(Date.now() + 4 * 24 * 3600 * 1000).toISOString(),
    });

    const res = http.post(`${BASE_URL}/v1/shipments`, body, {
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${TOKEN}` },
        tags: { name: 'create' },
    });

    if (res.status === 429) throttled.add(1);
    check(res, { 'created (201)': (r) => r.status === 201 });
}