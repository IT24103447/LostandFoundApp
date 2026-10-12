-- Story LF-267 / LF-268 / LF-269 - seeds the throwaway record pools that plan 18
-- (18-Spam-Case-Actions-StoryLF267-269.jmx) drives, and prints the three CSVs it reads.
--
-- Every row here is QA throwaway data under users named
-- jmeter.case<story>.<runTag>.<n>@example.com, where <runTag> is a fresh UUID per run. The tag
-- is what makes the script safely re-runnable: every insert and every CSV select filters on it,
-- so a second run never doubles up on the first run's pools, and the cleanup block at the
-- bottom can name exactly one run.
--
-- Why the seed is SQL and not a JMeter setUp thread: the plan stays readable XML and the exact
-- starting state of every record (status, kick_status, solve_result) is auditable here rather
-- than hidden in Groovy.
--
-- Run from the REPO ROOT:
--   docker exec -i mysql-dev mysql -uroot -yourpassword --table \
--     < tests/performance/AdminVerifyService/18-seed-case-action-records.sql
--
-- Set @pool_size to match -JTHREADS (default 10). The dismiss and solve pools need at least
-- one row per thread: those endpoints return 409 when a record is acted on twice, so a thread
-- drawing a repeated row fails the run. The open pool tolerates repeats.

SET @pool_size = 10;
SET @run_tag = LOWER(REPLACE(UUID(), '-', ''));
SET @now = UTC_TIMESTAMP(3);

-- ============================================================ LF-267: open pool
-- NEEDS_REVIEW records. OpenAsync moves these to UNDER_REVIEW.
INSERT INTO auth_service.users (id, email, password_hash, name, phone_no, is_email_verified, created_at, updated_at)
SELECT
  UUID(),
  CONCAT('jmeter.case267.', @run_tag, '.', nums.n, '@example.com'),
  'x',
  CONCAT('JMeter LF267 open ', @run_tag, ' ', nums.n),
  CONCAT('+', CONV(SUBSTRING(@run_tag, 1, 10), 16, 10), LPAD(nums.n, 4, '0')),
  1, @now, @now
FROM (
  SELECT 1 n UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5
  UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9 UNION ALL SELECT 10
  UNION ALL SELECT 11 UNION ALL SELECT 12 UNION ALL SELECT 13 UNION ALL SELECT 14
  UNION ALL SELECT 15 UNION ALL SELECT 16 UNION ALL SELECT 17 UNION ALL SELECT 18
  UNION ALL SELECT 19 UNION ALL SELECT 20
) nums
WHERE nums.n <= @pool_size;

INSERT INTO admin_verify_db.spam_records (id, user_id, collecting_user_id, score_a, status, collecting_until, kick_status, created_at, updated_at)
SELECT UUID(), u.id, NULL, 8, 'NEEDS_REVIEW', @now, 'NOT_REQUESTED', @now, @now
FROM auth_service.users u
WHERE u.email LIKE CONCAT('jmeter.case267.', @run_tag, '.%');

-- ========================================================== LF-268: dismiss pool
-- UNDER_REVIEW records. DismissAsync moves these to DISMISSED and 409s if already handled.
INSERT INTO auth_service.users (id, email, password_hash, name, phone_no, is_email_verified, created_at, updated_at)
SELECT
  UUID(),
  CONCAT('jmeter.case268.', @run_tag, '.', nums.n, '@example.com'),
  'x',
  CONCAT('JMeter LF268 dismiss ', @run_tag, ' ', nums.n),
  CONCAT('+', CONV(SUBSTRING(@run_tag, 1, 10), 16, 10), LPAD(nums.n + 20, 4, '0')),
  1, @now, @now
FROM (
  SELECT 1 n UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5
  UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9 UNION ALL SELECT 10
  UNION ALL SELECT 11 UNION ALL SELECT 12 UNION ALL SELECT 13 UNION ALL SELECT 14
  UNION ALL SELECT 15 UNION ALL SELECT 16 UNION ALL SELECT 17 UNION ALL SELECT 18
  UNION ALL SELECT 19 UNION ALL SELECT 20
) nums
WHERE nums.n <= @pool_size;

INSERT INTO admin_verify_db.spam_records (id, user_id, collecting_user_id, score_a, status, collecting_until, kick_status, created_at, updated_at)
SELECT UUID(), u.id, NULL, 8, 'UNDER_REVIEW', @now, 'NOT_REQUESTED', @now, @now
FROM auth_service.users u
WHERE u.email LIKE CONCAT('jmeter.case268.', @run_tag, '.%');

-- ============================================================= LF-269: solve pool
-- Two shapes per thread so one plan covers the plain solve and the resume:
--   kick=false, resume=false  UNDER_REVIEW   -> full runner sequence
--   kick=true,  resume=false  UNDER_REVIEW   -> runner sequence + kick-result step
--   kick=false, resume=true   PENDING_SOLVE  -> finish only (start-solve skipped by the plan)
INSERT INTO auth_service.users (id, email, password_hash, name, phone_no, is_email_verified, created_at, updated_at)
SELECT
  UUID(),
  CONCAT('jmeter.case269.', @run_tag, '.', nums.n, '@example.com'),
  'x',
  CONCAT('JMeter LF269 solve ', @run_tag, ' ', nums.n),
  CONCAT('+', CONV(SUBSTRING(@run_tag, 1, 10), 16, 10), LPAD(nums.n + 40, 4, '0')),
  1, @now, @now
FROM (
  SELECT 1 n UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5
  UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9 UNION ALL SELECT 10
  UNION ALL SELECT 11 UNION ALL SELECT 12 UNION ALL SELECT 13 UNION ALL SELECT 14
  UNION ALL SELECT 15 UNION ALL SELECT 16 UNION ALL SELECT 17 UNION ALL SELECT 18
  UNION ALL SELECT 19 UNION ALL SELECT 20
) nums
WHERE nums.n <= @pool_size;

INSERT INTO admin_verify_db.spam_records (id, user_id, collecting_user_id, score_a, status, collecting_until, kick_status, created_at, updated_at)
SELECT UUID(), u.id, NULL, 8, 'UNDER_REVIEW', @now, 'NOT_REQUESTED', @now, @now
FROM auth_service.users u
WHERE u.email LIKE CONCAT('jmeter.case269.', @run_tag, '.%');

-- Every solve record needs one linked listing row, because finish() refuses to solve while any
-- listing is NULL. The tracked_listings rows are also what the case detail joins against
-- (SpamCaseRepository.ReadDetailAsync), so they must exist for the detail to return listings.
INSERT INTO admin_verify_db.tracked_listings (listing_id, listing_type, user_id, posted_at, created_at)
SELECT UUID(), 'LOST', u.id, @now, @now
FROM auth_service.users u
WHERE u.email LIKE CONCAT('jmeter.case269.', @run_tag, '.%');

INSERT INTO admin_verify_db.spam_record_listings (spam_record_id, listing_id, added_at)
SELECT r.id, t.listing_id, @now
FROM admin_verify_db.spam_records r
JOIN auth_service.users u ON u.id = r.user_id
JOIN admin_verify_db.tracked_listings t ON t.user_id = r.user_id
WHERE u.email LIKE CONCAT('jmeter.case269.', @run_tag, '.%');

-- The last third of the pool is flipped to PENDING_SOLVE so resume=true rows exist. Rows are
-- picked by descending user id so this is stable regardless of insertion order, and the email
-- suffix is the row number so "resume the last third" is also "resume n > 2/3 * pool_size".
UPDATE admin_verify_db.spam_records r
JOIN auth_service.users u ON u.id = r.user_id
SET r.status = 'PENDING_SOLVE'
WHERE u.email LIKE CONCAT('jmeter.case269.', @run_tag, '.%')
  AND CAST(SUBSTRING_INDEX(SUBSTRING_INDEX(u.email, '@', 1), '.', -1) AS UNSIGNED)
      > FLOOR(@pool_size / 3) * 2;

-- ============================================================ The three plan CSVs
-- Copy each block (without its header line) into the matching CSV next to the plan.

SELECT CONCAT('-- run tag: ', @run_tag) AS note;

SELECT '-- storyLF267-open-records.csv --' AS csv;
SELECT r.id
FROM admin_verify_db.spam_records r
JOIN auth_service.users u ON u.id = r.user_id
WHERE u.email LIKE CONCAT('jmeter.case267.', @run_tag, '.%')
  AND r.status = 'NEEDS_REVIEW'
ORDER BY u.email;

SELECT '-- storyLF268-dismiss-records.csv --' AS csv;
SELECT r.id
FROM admin_verify_db.spam_records r
JOIN auth_service.users u ON u.id = r.user_id
WHERE u.email LIKE CONCAT('jmeter.case268.', @run_tag, '.%')
  AND r.status = 'UNDER_REVIEW'
ORDER BY u.email;

SELECT '-- storyLF269-solve-records.csv : recordId,listingId,kick,resume --' AS csv;
SELECT
  r.id,
  s.listing_id,
  CASE WHEN r.status = 'PENDING_SOLVE' THEN 'false' ELSE 'true'  END AS kick,
  CASE WHEN r.status = 'PENDING_SOLVE' THEN 'true'  ELSE 'false' END AS resume
FROM admin_verify_db.spam_records r
JOIN auth_service.users u ON u.id = r.user_id
JOIN admin_verify_db.spam_record_listings s ON s.spam_record_id = r.id
WHERE u.email LIKE CONCAT('jmeter.case269.', @run_tag, '.%')
  AND r.status IN ('UNDER_REVIEW', 'PENDING_SOLVE')
ORDER BY u.email;

-- ================================================================ Cleanup
-- Drop just this run's pools (substitute the run tag printed above):
--
--   SET @run_tag = '<tag from the note line>';
--   DELETE FROM admin_verify_db.spam_records   WHERE user_id IN
--     (SELECT id FROM auth_service.users WHERE email LIKE CONCAT('jmeter.case26%.', @run_tag, '.%'));
--   DELETE FROM admin_verify_db.tracked_listings WHERE user_id IN
--     (SELECT id FROM auth_service.users WHERE email LIKE CONCAT('jmeter.case26%.', @run_tag, '.%'));
--   DELETE FROM auth_service.users WHERE email LIKE CONCAT('jmeter.case26%.', @run_tag, '.%');