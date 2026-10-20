-- Optional illustrative DEVELOPMENT data. Select your Lending database in SSMS before running.
-- Adds a demo customer, account and two catalogue products. Does not modify existing records.
-- The application does not run this script automatically.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF NOT EXISTS (SELECT 1 FROM lending.Customer WHERE CustomerId = 'a1000000-0000-4000-8000-000000000001')
    INSERT INTO lending.Customer (CustomerId, CustomerNumber, FirstName, LastName, DateOfBirth, Email)
    VALUES ('a1000000-0000-4000-8000-000000000001', 'DEMO-CUS-001', 'Morgan', 'Reed', '1986-05-14', 'morgan@example.test');

IF NOT EXISTS (SELECT 1 FROM lending.Account WHERE AccountId = 'a2000000-0000-4000-8000-000000000001')
    INSERT INTO lending.Account (AccountId, CustomerId, AccountNumber)
    VALUES ('a2000000-0000-4000-8000-000000000001', 'a1000000-0000-4000-8000-000000000001', 'DEMO-ACC-001');

IF NOT EXISTS (SELECT 1 FROM lending.CreditProduct WHERE CreditProductId = 'a3000000-0000-4000-8000-000000000001')
    INSERT INTO lending.CreditProduct (CreditProductId, ProductCode, ProductName, ProductType)
    VALUES ('a3000000-0000-4000-8000-000000000001', 'DEMO-PL-001', 'Demo fixed loan', 'FixedLoan');

IF NOT EXISTS (SELECT 1 FROM lending.CreditProduct WHERE CreditProductId = 'a3000000-0000-4000-8000-000000000002')
    INSERT INTO lending.CreditProduct (CreditProductId, ProductCode, ProductName, ProductType)
    VALUES ('a3000000-0000-4000-8000-000000000002', 'DEMO-RC-001', 'Demo revolving credit', 'RevolvingCredit');

COMMIT TRANSACTION;
