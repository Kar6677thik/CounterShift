-- Legacy storage design (spec §6.1): one row per (counter key, granularity, bucket).
CREATE TABLE counter_buckets (
    counter_key  text        NOT NULL,
    granularity  smallint    NOT NULL,  -- bucket width in minutes: 1, 15, 60, 1440
    bucket_start timestamptz NOT NULL,  -- always UTC-aligned to the granularity
    count        bigint      NOT NULL CHECK (count >= 0),
    -- The primary key doubles as the lookup index: every read and write addresses rows by exactly
    -- these three columns.
    PRIMARY KEY (counter_key, granularity, bucket_start)
);

-- Retention cleanup asks "which buckets of granularity G started before T?". The primary key can't
-- answer that without scanning every counter, so we add this index. It costs extra work on every
-- insert, and that cost shows up in the storage-per-million-events numbers.
CREATE INDEX ix_counter_buckets_expiry ON counter_buckets (granularity, bucket_start);
