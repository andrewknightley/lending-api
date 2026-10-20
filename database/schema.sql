-- Exact table and index definitions recovered from lending-sql-infrastructure.zip (01 Oct 2026).
-- Only for a NEW disposable/development database. Use the separate SQL project/DACPAC for existing databases.

CREATE SCHEMA [lending] AUTHORIZATION [dbo];
GO

CREATE TABLE [lending].[Customer]
(
    [CustomerId] uniqueidentifier NOT NULL CONSTRAINT [DF_Customer_Id] DEFAULT NEWSEQUENTIALID(),
    [CustomerNumber] varchar(30) NOT NULL,
    [FirstName] nvarchar(100) NOT NULL,
    [LastName] nvarchar(100) NOT NULL,
    [DateOfBirth] date NOT NULL,
    [Email] nvarchar(254) NULL,
    [Status] varchar(20) NOT NULL CONSTRAINT [DF_Customer_Status] DEFAULT 'Active',
    [CreatedAtUtc] datetime2(7) NOT NULL CONSTRAINT [DF_Customer_Created] DEFAULT SYSUTCDATETIME(),
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Customer] PRIMARY KEY ([CustomerId]),
    CONSTRAINT [UQ_Customer_Number] UNIQUE ([CustomerNumber]),
    CONSTRAINT [CK_Customer_Status] CHECK ([Status] IN ('Active', 'Inactive', 'Deceased'))
);
GO

CREATE TABLE [lending].[Account]
(
    [AccountId] uniqueidentifier NOT NULL CONSTRAINT [DF_Account_Id] DEFAULT NEWSEQUENTIALID(),
    [CustomerId] uniqueidentifier NOT NULL,
    [AccountNumber] varchar(30) NOT NULL,
    [CurrencyCode] char(3) NOT NULL CONSTRAINT [DF_Account_Currency] DEFAULT 'GBP',
    [Status] varchar(20) NOT NULL CONSTRAINT [DF_Account_Status] DEFAULT 'Open',
    [OpenedAtUtc] datetime2(7) NOT NULL CONSTRAINT [DF_Account_Opened] DEFAULT SYSUTCDATETIME(),
    [ClosedAtUtc] datetime2(7) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Account] PRIMARY KEY ([AccountId]),
    CONSTRAINT [UQ_Account_Number] UNIQUE ([AccountNumber]),
    CONSTRAINT [FK_Account_Customer] FOREIGN KEY ([CustomerId]) REFERENCES [lending].[Customer] ([CustomerId]),
    CONSTRAINT [CK_Account_Currency] CHECK ([CurrencyCode] = 'GBP'),
    CONSTRAINT [CK_Account_Status] CHECK ([Status] IN ('Open', 'Suspended', 'Closed')),
    CONSTRAINT [CK_Account_Closure] CHECK
    (
        ([Status] = 'Closed' AND [ClosedAtUtc] IS NOT NULL AND [ClosedAtUtc] >= [OpenedAtUtc])
        OR ([Status] IN ('Open', 'Suspended') AND [ClosedAtUtc] IS NULL)
    )
);
GO

CREATE TABLE [lending].[CreditProduct]
(
    [CreditProductId] uniqueidentifier NOT NULL CONSTRAINT [DF_CreditProduct_Id] DEFAULT NEWSEQUENTIALID(),
    [ProductCode] varchar(30) NOT NULL,
    [ProductName] nvarchar(150) NOT NULL,
    [ProductType] varchar(20) NOT NULL,
    [IsAvailable] bit NOT NULL CONSTRAINT [DF_CreditProduct_Available] DEFAULT 1,
    [CreatedAtUtc] datetime2(7) NOT NULL CONSTRAINT [DF_CreditProduct_Created] DEFAULT SYSUTCDATETIME(),
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_CreditProduct] PRIMARY KEY ([CreditProductId]),
    CONSTRAINT [UQ_CreditProduct_Code] UNIQUE ([ProductCode]),
    CONSTRAINT [CK_CreditProduct_Type] CHECK ([ProductType] IN ('FixedLoan', 'RevolvingCredit'))
);
GO

CREATE TABLE [lending].[CreditAgreement]
(
    [CreditAgreementId] uniqueidentifier NOT NULL CONSTRAINT [DF_CreditAgreement_Id] DEFAULT NEWSEQUENTIALID(),
    [AgreementNumber] varchar(30) NOT NULL,
    [AccountId] uniqueidentifier NOT NULL,
    [CreditProductId] uniqueidentifier NOT NULL,
    -- Accepted terms are a snapshot, independent of later catalogue changes.
    [AgreementType] varchar(20) NOT NULL,
    [AgreedAmount] decimal(19,4) NOT NULL,
    [AnnualInterestRatePercent] decimal(9,6) NOT NULL,
    [TermMonths] int NULL,
    [Status] varchar(20) NOT NULL CONSTRAINT [DF_CreditAgreement_Status] DEFAULT 'Pending',
    [CreatedAtUtc] datetime2(7) NOT NULL CONSTRAINT [DF_CreditAgreement_Created] DEFAULT SYSUTCDATETIME(),
    [ActivatedAtUtc] datetime2(7) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_CreditAgreement] PRIMARY KEY ([CreditAgreementId]),
    CONSTRAINT [UQ_CreditAgreement_Number] UNIQUE ([AgreementNumber]),
    CONSTRAINT [FK_CreditAgreement_Account] FOREIGN KEY ([AccountId]) REFERENCES [lending].[Account] ([AccountId]),
    CONSTRAINT [FK_CreditAgreement_Product] FOREIGN KEY ([CreditProductId]) REFERENCES [lending].[CreditProduct] ([CreditProductId]),
    CONSTRAINT [CK_CreditAgreement_Amount] CHECK ([AgreedAmount] > 0),
    CONSTRAINT [CK_CreditAgreement_Rate] CHECK ([AnnualInterestRatePercent] >= 0),
    CONSTRAINT [CK_CreditAgreement_Status] CHECK ([Status] IN ('Pending', 'Active', 'Settled', 'Cancelled')),
    CONSTRAINT [CK_CreditAgreement_Terms] CHECK
    (
        ([AgreementType] = 'FixedLoan' AND [TermMonths] IS NOT NULL AND [TermMonths] > 0)
        OR ([AgreementType] = 'RevolvingCredit' AND [TermMonths] IS NULL)
    ),
    CONSTRAINT [CK_CreditAgreement_Activation] CHECK
    (
        ([ActivatedAtUtc] IS NULL OR [ActivatedAtUtc] >= [CreatedAtUtc])
        AND ([Status] NOT IN ('Active', 'Settled') OR [ActivatedAtUtc] IS NOT NULL)
    )
);
GO

CREATE INDEX [IX_Account_CustomerId] ON [lending].[Account] ([CustomerId]);
GO

CREATE INDEX [IX_CreditAgreement_AccountId] ON [lending].[CreditAgreement] ([AccountId]);
GO

CREATE INDEX [IX_CreditAgreement_CreditProductId] ON [lending].[CreditAgreement] ([CreditProductId]);
GO
