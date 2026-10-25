// Creates shipments through the Public API at a steady rate, spread over the three carriers.
// Each parcel is first booked with its (mock) carrier, so the carrier's clock starts at creation,
// as with a real carrier, and not at our first poll.
// k6 measures the API; the rest of the journey is read from Aspire's metrics
// (gateway.event.age, gateway.poll.gap, gateway.polls, shipment.time_to_delivered).
//
//   $env:TOKEN = "<merchant-1 token>"
//   k6 run -e RATE=1 -e DURATION=3m tests/load/shipments.js

import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';
import exec from 'k6/execution';

const BASE_URL = __ENV.BASE_URL || 'https://localhost:7120';
const CARRIERS_URL = __ENV.CARRIERS_URL || 'https://localhost:7049';
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
        http_req_failed: ['rate<0.01'],                  // a failed request has no meaningful latency
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
    const trackingNumber = `${carrier.prefix}${RUN}${String(n).padStart(6, '0')}`;

    // 1. The merchant books the parcel with the carrier: its journey starts now.
    const booked = http.post(`${CARRIERS_URL}/admin/journeys/${carrier.name.toLowerCase()}/${trackingNumber}`, null, {
        tags: { name: 'book' },
    });
    check(booked, { 'booked with carrier (200)': (r) => r.status === 200 });

    // 2. The merchant registers it with CarrierHub.
    const body = JSON.stringify({
        merchantReference: `LOAD-${RUN}-${n}`,
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