/*
  Bulk seed Inbox / Sent / Completed for performance testing (e.g. 10,000 rows each) -- Postgres
  Ported from scripts/Seed_LegacyMailbox_PerformanceTest.sql -- Phase 3.

  Run on TENANT database after workflow tables exist.
  Uses same column shapes as WorkflowLegacyMailboxSyncService (workflowInstanceId = D-format GUID string).

  SQL Server's `SELECT TOP (@Rows) ROW_NUMBER() ... FROM sys.all_objects CROSS JOIN
  sys.all_objects` tally-table trick for generating N sequential numbers has a much
  more direct Postgres equivalent: generate_series(1, n).

  Edit the CONFIGURE section below, then run.
*/

DO $$
DECLARE
    workflow_id          uuid := '00000000-0000-0000-0000-000000000000'; -- your workflow id
    workflow_instance_id uuid := '00000000-0000-0000-0000-000000000001'; -- your instance id
    p_user_id            uuid := '00000000-0000-0000-0000-000000000002'; -- current user
    rows_per_table        integer := 10000;

    suffix       text := left(replace(workflow_id::text, '-', ''), 8);
    workflow_id_str text := workflow_id::text;
    instance_id_str text := workflow_instance_id::text;
    user_id_str     text := p_user_id::text;

    tbl_rec record;
    target_table text;
    mailbox_kind text;
    idx_name text;
BEGIN
    RAISE NOTICE 'WorkflowId: %', workflow_id_str;
    RAISE NOTICE 'InstanceId: %', instance_id_str;
    RAISE NOTICE 'Suffix: %', suffix;

    -- ---------------------------------------------------------------------------
    -- 1) Indexes (fast list: workflowInstanceId + transaction_createdAt)
    -- ---------------------------------------------------------------------------
    FOR tbl_rec IN
        SELECT * FROM (VALUES
            ('Inbox_' || suffix),
            ('Sent_' || suffix),
            ('Completed_' || suffix)
        ) AS v(table_name)
    LOOP
        idx_name := 'IX_' || tbl_rec.table_name || '_Instance_Created';
        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'workflow' AND table_name = tbl_rec.table_name)
           AND NOT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'workflow' AND indexname = idx_name)
        THEN
            EXECUTE format(
                'CREATE INDEX %I ON workflow.%I ("workflowInstanceId", "transaction_createdAt" DESC, "id" DESC) INCLUDE ("userId", "transaction_createdBy", "transactionId", "name", "referenceNumber")',
                idx_name, tbl_rec.table_name);
            RAISE NOTICE 'Created index %', idx_name;
        END IF;
    END LOOP;

    -- ---------------------------------------------------------------------------
    -- 2) Seed helper: insert rows_per_table into each mailbox table
    -- ---------------------------------------------------------------------------
    FOR tbl_rec IN
        SELECT * FROM (VALUES
            ('Inbox_' || suffix, 'Inbox'),
            ('Sent_' || suffix, 'Sent'),
            ('Completed_' || suffix, 'Completed')
        ) AS v(table_name, kind)
    LOOP
        target_table := tbl_rec.table_name;
        mailbox_kind := tbl_rec.kind;

        IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'workflow' AND table_name = target_table) THEN
            RAISE EXCEPTION 'Table workflow.% does not exist. Create workflow or run provision-tables first.', target_table;
        END IF;

        RAISE NOTICE 'Seeding % (% rows)...', target_table, rows_per_table;

        EXECUTE format($fmt$
            INSERT INTO workflow.%1$I (
                "userId", "groupId", "workflowId", "name", "workflowInstanceId", "referenceNumber",
                "createdAtUtc", "startedAtUtc", "completedAtUtc", "context",
                "transactionId", "activityId", "ruleId", "stageType", "stage", "review",
                "transaction_createdAt", "transaction_createdBy", "transaction_createdByEmail",
                "transaction_modifiedAt", "transaction_modifiedBy",
                "formId", "formEntryId", "commentsCount", "attachmentCount", "createdByName"
            )
            SELECT
                %2$L,
                NULL,
                %3$L,
                'Perf Test ' || %4$L,
                %5$L,
                'REF-' || lpad(n::text, 6, '0'),
                now() - (n || ' seconds')::interval,
                now() - (n || ' seconds')::interval,
                CASE WHEN %4$L = 'Completed' THEN now() - (n || ' seconds')::interval ELSE NULL END,
                'load test',
                gen_random_uuid()::text,
                'act-' || n::text,
                NULL,
                CASE WHEN %4$L = 'Inbox' THEN 'USER' WHEN %4$L = 'Sent' THEN 'PROCESS' ELSE 'END' END,
                'Stage ' || ((n %% 5) + 1)::text,
                'pending',
                now() - (n || ' seconds')::interval,
                %2$L,
                'user@example.com',
                now(),
                %2$L,
                NULL,
                n::text,
                0,
                0,
                'Seed User'
            FROM generate_series(1, %6$L::int) AS n
        $fmt$, target_table, user_id_str, workflow_id_str, mailbox_kind, instance_id_str, rows_per_table);
    END LOOP;

    -- ---------------------------------------------------------------------------
    -- 3) Verify counts for this instance + user
    -- ---------------------------------------------------------------------------
    RAISE NOTICE 'Done. Test API:';
    RAISE NOTICE '  GET /api/workflows/inbox?workflowId=...&instanceId=...&pageNumber=1&pageSize=50&skipTotal=true';
END $$;
