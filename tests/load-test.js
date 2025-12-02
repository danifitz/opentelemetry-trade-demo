import http from 'k6/http';
import { check, sleep } from 'k6';
import { randomString } from 'https://jslib.k6.io/k6-utils/1.2.0/index.js';

// Test configuration
export const options = {
  // Single virtual user
  vus: 1,
  // Run for 3 minutes
  duration: '3m',
  // Thresholds for pass/fail
  thresholds: {
    http_req_duration: ['p(95)<2000'], // 95% of requests should be below 2s
    http_req_failed: ['rate<0.1'],      // Less than 10% failure rate
  },
};

// k6 shares network namespace with servicebus-emulator, so services are on localhost
const BASE_URL = __ENV.BASE_URL || 'http://localhost:5001';
const SETTLEMENT_URL = __ENV.SETTLEMENT_URL || 'http://localhost:5002';

// Sample instruments for variety
const instruments = [
  'CRUDE-OIL-JAN25',
  'NATURAL-GAS-FEB25',
  'BRENT-MAR25',
  'WTI-APR25',
  'HEATING-OIL-MAY25',
  'GASOLINE-JUN25',
];

// Sample counterparties
const counterparties = [
  'ACME Corp',
  'Global Energy',
  'Power Trading Ltd',
  'Commodity Partners',
  'Energy Solutions',
];

// Generate a random trade
function generateTrade() {
  return {
    instrument: instruments[Math.floor(Math.random() * instruments.length)],
    quantity: Math.floor(Math.random() * 10000) + 100,
    price: (Math.random() * 100 + 10).toFixed(2),
    counterparty: counterparties[Math.floor(Math.random() * counterparties.length)],
  };
}

export default function () {
  // Step 1: Create a new trade
  const trade = generateTrade();
  
  const createResponse = http.post(
    `${BASE_URL}/api/trades`,
    JSON.stringify(trade),
    {
      headers: {
        'Content-Type': 'application/json',
      },
      tags: { name: 'CreateTrade' },
    }
  );

  const createSuccess = check(createResponse, {
    'trade created successfully': (r) => r.status === 201,
    'response has trade ID': (r) => {
      try {
        const body = JSON.parse(r.body);
        return body.tradeId !== undefined;
      } catch {
        return false;
      }
    },
  });

  let tradeId = null;
  if (createSuccess) {
    try {
      const body = JSON.parse(createResponse.body);
      tradeId = body.tradeId;
      console.log(`Created trade: ${tradeId} - ${trade.instrument}`);
    } catch (e) {
      console.error('Failed to parse create response');
    }
  }

  // Wait a bit before next operation
  sleep(1);

  // Step 2: Get all trades
  const listResponse = http.get(`${BASE_URL}/api/trades`, {
    tags: { name: 'ListTrades' },
  });

  check(listResponse, {
    'list trades successful': (r) => r.status === 200,
    'response is array': (r) => {
      try {
        const body = JSON.parse(r.body);
        return Array.isArray(body);
      } catch {
        return false;
      }
    },
  });

  sleep(0.5);

  // Step 3: Get specific trade if we have an ID
  if (tradeId) {
    const getResponse = http.get(`${BASE_URL}/api/trades/${tradeId}`, {
      tags: { name: 'GetTrade' },
    });

    check(getResponse, {
      'get trade successful': (r) => r.status === 200,
      'trade ID matches': (r) => {
        try {
          const body = JSON.parse(r.body);
          return body.tradeId === tradeId;
        } catch {
          return false;
        }
      },
    });

    sleep(0.5);

    // Step 4: Check settlement status (give it time to process)
    sleep(2);

    const settlementResponse = http.get(
      `${SETTLEMENT_URL}/api/settlement/${tradeId}/status`,
      {
        tags: { name: 'CheckSettlement' },
      }
    );

    check(settlementResponse, {
      'settlement check successful': (r) => r.status === 200 || r.status === 404,
      'trade is being processed': (r) => {
        if (r.status === 404) return true; // Trade might not be in Oracle yet
        try {
          const body = JSON.parse(r.body);
          return body.status !== undefined;
        } catch {
          return false;
        }
      },
    });
  }

  // Step 5: Health checks
  const tradeHealthResponse = http.get(`${BASE_URL}/health`, {
    tags: { name: 'TradeServiceHealth' },
  });

  check(tradeHealthResponse, {
    'trade service healthy': (r) => r.status === 200,
  });

  const settlementHealthResponse = http.get(`${SETTLEMENT_URL}/health`, {
    tags: { name: 'SettlementServiceHealth' },
  });

  check(settlementHealthResponse, {
    'settlement service healthy': (r) => r.status === 200,
  });

  // Wait before next iteration
  sleep(2);
}

// Summary output
export function handleSummary(data) {
  console.log('\n========== Load Test Summary ==========');
  console.log(`Total requests: ${data.metrics.http_reqs.values.count}`);
  console.log(`Average duration: ${data.metrics.http_req_duration.values.avg.toFixed(2)}ms`);
  console.log(`95th percentile: ${data.metrics.http_req_duration.values['p(95)'].toFixed(2)}ms`);
  console.log(`Failed requests: ${data.metrics.http_req_failed.values.passes}`);
  console.log('========================================\n');
  
  return {
    stdout: JSON.stringify(data, null, 2),
  };
}

