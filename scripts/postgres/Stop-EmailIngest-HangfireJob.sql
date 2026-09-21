-- Temporarily stop Hangfire email-ingest recurring job (catalog DB).
-- Job id: email-ingest-poll  |  Display: "Email ingest · schedule mailboxes"
-- After this, also set EmailIngest:HangfireEnabled=false and restart API
-- so Program.cs does not re-register it.

-- Recurring job definition
DELETE FROM hangfire.hash
WHERE key = 'recurring-job:email-ingest-poll';

-- Recurring job index
DELETE FROM hangfire.set
WHERE key = 'recurring-jobs'
  AND value = 'email-ingest-poll';

-- Optional: drop already-queued email-ingest jobs still waiting (safe for temp stop)
DELETE FROM hangfire.job
WHERE invocationdata::text ILIKE '%RunEmailIngestPollJob%'
  AND statename IN ('Enqueued', 'Scheduled', 'Processing');

-- Confirm gone
SELECT key, value FROM hangfire.set WHERE key = 'recurring-jobs' AND value = 'email-ingest-poll';
SELECT key FROM hangfire.hash WHERE key = 'recurring-job:email-ingest-poll';
