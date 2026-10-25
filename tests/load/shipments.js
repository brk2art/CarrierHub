// Creates shipments through the Public API at a steady rate, spread over the chosen carriers.
// Each parcel is first booked with its (mock) carrier, so the carrier's clock starts at creation,
// as with a real carrier, and not at our first poll.
// k6 measures the API; the rest of the journey is read from Aspire's metrics.
//
// One rate:
//   k6 run -e RATE=1 -e DURATION=3m tests/load/shipments.js
// Several rates one after another (each its own scenario in the summary), webhook carrier only:
//   k6 run -e CARRIERS=ParcelGo -e STAGES=5,10,20 -e STAGE_DURATION=4m --summary-mode=full tests/load/shipments.js

import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';
import exec from 'k6/execution';

const BASE_URL = __ENV.BASE_URL || 'https://localhost:7120';
const CARRIERS_URL = __ENV.CARRIERS_URL || 'https://localhost:7049';
const TOKEN = __ENV.TOKEN;
const RATE = Number(__ENV.RATE || 2);                 // new shipments per second (single stage)
const DURATION = __ENV.DURATION || '1m';
const STAGES = (__ENV.STAGES || '').split(',').filter(Boolean).map(Number);
const STAGE_DURATION = __ENV.STAGE_DURATION || '4m';
// Part of every order and tracking number, so runs never collide with each other or with earlier data.
const RUN = __ENV.RUN || String(Date.now()).slice(-6);

const allCarriers = [
    { name: 'FastShip', prefix: 'FS' },
    { name: 'NordPost', prefix: 'NP' },
    { name: 'ParcelGo', prefix: 'PG' },
];
const wanted = (__ENV.CARRIERS || 'FastShip,NordPost,ParcelGo').split(',').map((c) => c.trim().toLowerCase());
const carriers = allCarriers.filter((c) => wanted.includes(c.name.toLowerCase()));

const throttled = new Counter('throttled');           // 429s from the per-merchant rate limit

function seconds(duration) {
    const m = /^(\d+)(s|m)$/.exec(duration);
    if (!m) throw new Error(`Use durations like 30s or 4m, not "${duration}"`);
    return Number(m[1]) * (m[2] === 'm' ? 60 : 1);
}

// A fixed number of new shipments per second, however slow the API gets.
// (A fixed number of users would slow down with the API and hide the problem.)
function stage(rate, duration, startTime, index) {
    return {
        executor: 'constant-arrival-rate',
        rate,
        timeUnit: '1s',
        duration,
        startTime,
        preAllocatedVUs: Math.max(20, rate * 2),
        maxVUs: Math.max(200, rate * 10),
        env: { STAGE: String(index) },                  // keeps tracking numbers unique across stages
    };
}

function buildScenarios() {
    if (STAGES.length === 0)
        return { create: stage(RATE, DURATION, '0s', 0) };

    const scenarios = {};
    STAGES.forEach((rate, i) => {
        scenarios[`rate_${String(rate).padStart(3, '0')}`] =
            stage(rate, STAGE_DURATION, `${i * seconds(STAGE_DURATION)}s`, i + 1);
    });
    return scenarios;
}

export const options = {
    insecureSkipTLSVerify: true,                      // the ASP.NET development certificate on localhost
    scenarios: buildScenarios(),
    thresholds: {
        http_req_failed: ['rate<0.01'],                  // a failed request has no meaningful latency
        'http_req_duration{name:create}': ['p(95)<500'],
        checks: ['rate>0.99'],
    },
};

export function setup() {
    if (!TOKEN) throw new Error('Set the merchant token first: $env:TOKEN = "<token>"');
    if (carriers.length === 0) throw new Error(`No known carrier in CARRIERS="${__ENV.CARRIERS}"`);
    const plan = STAGES.length ? `${STAGES.join(' → ')} shipments/s, ${STAGE_DURATION} each` : `${RATE} shipments/s for ${DURATION}`;
    console.log(`Run ${RUN}: ${plan}, carriers: ${carriers.map((c) => c.name).join(', ')}`);
}

export default function () {
    const n = exec.scenario.iterationInTest;
    const carrier = carriers[n % carriers.length];
    const trackingNumber = `${carrier.prefix}${RUN}${__ENV.STAGE}${String(n).padStart(6, '0')}`;

    // 1. The merchant books the parcel with the carrier: its journey starts now.
    const booked = http.post(`${CARRIERS_URL}/admin/journeys/${carrier.name.toLowerCase()}/${trackingNumber}`, null, {
        tags: { name: 'book' },
    });
    check(booked, { 'booked with carrier (200)': (r) => r.status === 200 });

    // 2. The merchant registers it with CarrierHub.
    const body = JSON.stringify({
        merchantReference: `LOAD-${RUN}-${__ENV.STAGE}-${n}`,
        carrier: carrier.name,
        trackingNumber,
        promisedBy: new Date(Date.now() + 4 * 24 * 3600 * 1000).toISOString(),
    });

    const res = http.post(`${BASE_URL}/v1/shipments`, body, {
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${TOKEN}` },
        tags: { name: 'create' },
    });

    if (res.status === 429) throttled.add(1);
    check(res, { 'created (201)': (r) => r.status === 201 });
}