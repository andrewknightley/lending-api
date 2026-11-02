-- Run explicitly AFTER DACPAC publication, with old and new agreement writers stopped.
-- Transactional and repeatable: only agreements without a stream receive a baseline.
-- The rowversion and current projection are deliberately left unchanged.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @importedAt datetime2(7) = SYSUTCDATETIME();
DECLARE @missing TABLE (CreditAgreementId uniqueidentifier PRIMARY KEY);
INSERT INTO @missing
SELECT a.CreditAgreementId
FROM lending.CreditAgreement a WITH (TABLOCKX, HOLDLOCK)
WHERE NOT EXISTS (SELECT 1 FROM lending.AgreementStream s WITH (UPDLOCK, HOLDLOCK) WHERE s.CreditAgreementId = a.CreditAgreementId);

INSERT INTO lending.AgreementStream (CreditAgreementId, AgreementNumber, Version)
SELECT a.CreditAgreementId, a.AgreementNumber, 1
FROM lending.CreditAgreement a INNER JOIN @missing m ON m.CreditAgreementId = a.CreditAgreementId;

INSERT INTO lending.AgreementEvent (EventId, CreditAgreementId, StreamVersion, EventType, SchemaVersion, OccurredAtUtc, Payload)
SELECT NEWID(), a.CreditAgreementId, 1, 'AgreementImported', 1, @importedAt,
    (SELECT a.CreditAgreementId AS creditAgreementId,
        CONVERT(varchar(33), @importedAt, 126) + 'Z' AS occurredAtUtc,
        a.AgreementNumber AS agreementNumber, a.AccountId AS accountId, a.CreditProductId AS creditProductId,
        a.AgreementType AS agreementType, a.AgreedAmount AS agreedAmount,
        a.AnnualInterestRatePercent AS annualInterestRatePercent, a.TermMonths AS termMonths,
        a.Status AS status, CONVERT(varchar(33), a.CreatedAtUtc, 126) + 'Z' AS createdAtUtc,
        CASE WHEN a.ActivatedAtUtc IS NULL THEN NULL ELSE CONVERT(varchar(33), a.ActivatedAtUtc, 126) + 'Z' END AS activatedAtUtc
     FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)
FROM lending.CreditAgreement a INNER JOIN @missing m ON m.CreditAgreementId = a.CreditAgreementId;
COMMIT;
