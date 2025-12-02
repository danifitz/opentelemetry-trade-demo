-- PostgreSQL Database Initialization Script
-- Creates the trades table with W3C Trace Context columns

-- Create trades table with trace context for cross-service trace propagation
CREATE TABLE IF NOT EXISTS trades (
    trade_id VARCHAR(50) PRIMARY KEY,
    instrument VARCHAR(100) NOT NULL,
    quantity DECIMAL(18, 4) NOT NULL,
    price DECIMAL(18, 4) NOT NULL,
    counterparty VARCHAR(100) NOT NULL,
    trade_date TIMESTAMPTZ NOT NULL,
    status VARCHAR(20) NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW() NOT NULL,
    settled_at TIMESTAMPTZ,
    
    -- W3C Trace Context columns for trace propagation across database operations
    -- traceparent format: "00-{trace-id}-{span-id}-{trace-flags}"
    -- Example: "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"
    trace_parent VARCHAR(100),
    
    -- Optional trace state for vendor-specific information
    trace_state VARCHAR(500)
);

-- Create indexes for common queries
CREATE INDEX IF NOT EXISTS idx_trades_status ON trades(status);
CREATE INDEX IF NOT EXISTS idx_trades_date ON trades(trade_date);
CREATE INDEX IF NOT EXISTS idx_trades_created ON trades(created_at);

-- Insert sample data (without trace context - these are pre-existing trades)
INSERT INTO trades (trade_id, instrument, quantity, price, counterparty, trade_date, status, created_at)
VALUES 
    ('sample001', 'CRUDE-OIL-JAN25', 1000, 75.50, 'ACME Corp', NOW(), 'Settled', NOW()),
    ('sample002', 'NATURAL-GAS-FEB25', 5000, 3.25, 'Global Energy', NOW(), 'Settled', NOW())
ON CONFLICT (trade_id) DO NOTHING;

-- Verify table creation
SELECT 'trades table created with ' || COUNT(*) || ' initial records' AS status FROM trades;

