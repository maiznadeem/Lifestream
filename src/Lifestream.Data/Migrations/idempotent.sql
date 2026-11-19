IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE TABLE [AuditLog] (
        [Id] int NOT NULL IDENTITY,
        [StaffId] int NULL,
        [Action] varchar(10) NOT NULL,
        [Entity] varchar(30) NOT NULL,
        [EntityId] int NOT NULL,
        [OccurredAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_AuditLog] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_AuditLog_Action] CHECK ([Action] IN ('Insert', 'Update', 'Delete'))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE TABLE [DemoState] (
        [Id] int NOT NULL,
        [LastResetDate] date NULL,
        [LastForcedResetAtUtc] datetime2(3) NULL,
        CONSTRAINT [PK_DemoState] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_DemoState_SingleRow] CHECK ([Id] = 1)
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE TABLE [Donor] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(100) COLLATE Latin1_General_100_CI_AI NOT NULL,
        [DateOfBirth] date NOT NULL,
        [Gender] varchar(16) NOT NULL,
        [BloodType] varchar(5) NOT NULL,
        [Phone] varchar(20) NULL,
        [Address] nvarchar(200) NULL,
        [Frequency] varchar(16) NOT NULL,
        [LastDonated] date NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_Donor] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Donor_BloodType] CHECK ([BloodType] IN ('APos', 'ANeg', 'BPos', 'BNeg', 'ABPos', 'ABNeg', 'OPos', 'ONeg')),
        CONSTRAINT [CK_Donor_Frequency] CHECK ([Frequency] IN ('Regular', 'Occasional')),
        CONSTRAINT [CK_Donor_Gender] CHECK ([Gender] IN ('Male', 'Female', 'Other'))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE TABLE [Patient] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(100) COLLATE Latin1_General_100_CI_AI NOT NULL,
        [DateOfBirth] date NOT NULL,
        [Gender] varchar(16) NOT NULL,
        [BloodType] varchar(5) NOT NULL,
        [Phone] varchar(20) NULL,
        [Address] nvarchar(200) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_Patient] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Patient_BloodType] CHECK ([BloodType] IN ('APos', 'ANeg', 'BPos', 'BNeg', 'ABPos', 'ABNeg', 'OPos', 'ONeg')),
        CONSTRAINT [CK_Patient_Gender] CHECK ([Gender] IN ('Male', 'Female', 'Other'))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE TABLE [Staff] (
        [Id] int NOT NULL IDENTITY,
        [DisplayName] nvarchar(100) COLLATE Latin1_General_100_CI_AI NOT NULL,
        [Username] nvarchar(50) COLLATE Latin1_General_100_CI_AS NOT NULL,
        [PasswordHash] varchar(256) NOT NULL,
        [Gender] varchar(16) NOT NULL,
        [DateOfBirth] date NOT NULL,
        [Designation] varchar(16) NOT NULL,
        [Shift] varchar(16) NOT NULL,
        [Role] varchar(16) NOT NULL,
        [Salary] int NULL,
        [Phone] varchar(20) NULL,
        [IsProtected] bit NOT NULL,
        [IsDemo] bit NOT NULL,
        [HasPhoto] bit NOT NULL,
        [FailedLoginCount] int NOT NULL,
        [LockoutEndUtc] datetime2(3) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_Staff] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Staff_Designation] CHECK ([Designation] IN ('Admin', 'Doctor', 'Worker', 'Assistant')),
        CONSTRAINT [CK_Staff_Gender] CHECK ([Gender] IN ('Male', 'Female', 'Other')),
        CONSTRAINT [CK_Staff_PasswordHash] CHECK (LEN([PasswordHash]) BETWEEN 80 AND 256 AND [PasswordHash] LIKE 'AQAAAAI%' COLLATE Latin1_General_100_BIN2),
        CONSTRAINT [CK_Staff_Role] CHECK ([Role] IN ('Admin', 'Staff')),
        CONSTRAINT [CK_Staff_Salary] CHECK ([Salary] IS NULL OR [Salary] >= 0),
        CONSTRAINT [CK_Staff_Shift] CHECK ([Shift] IN ('Morning', 'Evening', 'FullTime'))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE TABLE [Appointment] (
        [Id] int NOT NULL IDENTITY,
        [DonorId] int NOT NULL,
        [PlacedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [SlotStartUtc] datetime2(3) NOT NULL,
        [Status] varchar(16) NOT NULL,
        [OverrideReason] nvarchar(300) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_Appointment] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Appointment_Status] CHECK ([Status] IN ('Pending', 'Completed', 'Cancelled', 'NoShow')),
        CONSTRAINT [FK_Appointment_Donor_DonorId] FOREIGN KEY ([DonorId]) REFERENCES [Donor] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE TABLE [Request] (
        [Id] int NOT NULL IDENTITY,
        [PatientId] int NOT NULL,
        [BloodType] varchar(5) NOT NULL,
        [Units] int NOT NULL,
        [PlacedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [RequiredBy] date NOT NULL,
        [Status] varchar(16) NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_Request] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Request_BloodType] CHECK ([BloodType] IN ('APos', 'ANeg', 'BPos', 'BNeg', 'ABPos', 'ABNeg', 'OPos', 'ONeg')),
        CONSTRAINT [CK_Request_Status] CHECK ([Status] IN ('Pending', 'Fulfilled', 'Cancelled')),
        CONSTRAINT [CK_Request_Units] CHECK ([Units] > 0),
        CONSTRAINT [FK_Request_Patient_PatientId] FOREIGN KEY ([PatientId]) REFERENCES [Patient] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE TABLE [RefreshToken] (
        [Id] int NOT NULL IDENTITY,
        [StaffId] int NOT NULL,
        [FamilyId] uniqueidentifier NOT NULL,
        [TokenHash] varchar(128) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [ExpiresAtUtc] datetime2(3) NOT NULL,
        [RevokedAtUtc] datetime2(3) NULL,
        CONSTRAINT [PK_RefreshToken] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RefreshToken_Staff_StaffId] FOREIGN KEY ([StaffId]) REFERENCES [Staff] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE TABLE [StaffPhoto] (
        [StaffId] int NOT NULL,
        [Content] varbinary(max) NOT NULL,
        [ContentType] varchar(50) NOT NULL,
        [Width] int NOT NULL,
        [Height] int NOT NULL,
        [UpdatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_StaffPhoto] PRIMARY KEY ([StaffId]),
        CONSTRAINT [CK_StaffPhoto_Pixels] CHECK ([Width] BETWEEN 1 AND 256 AND [Height] BETWEEN 1 AND 256),
        CONSTRAINT [CK_StaffPhoto_Size] CHECK (DATALENGTH([Content]) BETWEEN 1 AND 30720),
        CONSTRAINT [FK_StaffPhoto_Staff_StaffId] FOREIGN KEY ([StaffId]) REFERENCES [Staff] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE TABLE [Receipt] (
        [Id] int NOT NULL IDENTITY,
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [Kind] varchar(16) NOT NULL,
        [BloodType] varchar(5) NOT NULL,
        [Units] int NOT NULL,
        [StaffId] int NOT NULL,
        [AppointmentId] int NULL,
        [RequestId] int NULL,
        [Notes] nvarchar(500) NULL,
        CONSTRAINT [PK_Receipt] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Receipt_BloodType] CHECK ([BloodType] IN ('APos', 'ANeg', 'BPos', 'BNeg', 'ABPos', 'ABNeg', 'OPos', 'ONeg')),
        CONSTRAINT [CK_Receipt_Kind] CHECK ([Kind] IN ('Donation', 'Issue')),
        CONSTRAINT [CK_Receipt_Links] CHECK (([Kind] = 'Donation' AND [AppointmentId] IS NOT NULL AND [RequestId] IS NULL) OR ([Kind] = 'Issue' AND [RequestId] IS NOT NULL AND [AppointmentId] IS NULL)),
        CONSTRAINT [CK_Receipt_Units] CHECK ([Units] > 0),
        CONSTRAINT [FK_Receipt_Appointment_AppointmentId] FOREIGN KEY ([AppointmentId]) REFERENCES [Appointment] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Receipt_Request_RequestId] FOREIGN KEY ([RequestId]) REFERENCES [Request] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Receipt_Staff_StaffId] FOREIGN KEY ([StaffId]) REFERENCES [Staff] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE TABLE [BloodBag] (
        [Id] int NOT NULL IDENTITY,
        [BloodType] varchar(5) NOT NULL,
        [CollectedAtUtc] datetime2(3) NOT NULL,
        [ExpiresOn] date NOT NULL,
        [Status] varchar(16) NOT NULL,
        [DonationReceiptId] int NOT NULL,
        [IssueReceiptId] int NULL,
        CONSTRAINT [PK_BloodBag] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_BloodBag_BloodType] CHECK ([BloodType] IN ('APos', 'ANeg', 'BPos', 'BNeg', 'ABPos', 'ABNeg', 'OPos', 'ONeg')),
        CONSTRAINT [CK_BloodBag_IssueLink] CHECK (([Status] = 'Issued' AND [IssueReceiptId] IS NOT NULL) OR ([Status] <> 'Issued' AND [IssueReceiptId] IS NULL)),
        CONSTRAINT [CK_BloodBag_Status] CHECK ([Status] IN ('Available', 'Issued', 'Discarded')),
        CONSTRAINT [FK_BloodBag_Receipt_DonationReceiptId] FOREIGN KEY ([DonationReceiptId]) REFERENCES [Receipt] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_BloodBag_Receipt_IssueReceiptId] FOREIGN KEY ([IssueReceiptId]) REFERENCES [Receipt] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Appointment_DonorId] ON [Appointment] ([DonorId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Appointment_PlacedAtUtc] ON [Appointment] ([PlacedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Appointment_SlotStartUtc] ON [Appointment] ([SlotStartUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Appointment_Status_SlotStartUtc] ON [Appointment] ([Status], [SlotStartUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_AuditLog_Entity_EntityId] ON [AuditLog] ([Entity], [EntityId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_AuditLog_OccurredAtUtc] ON [AuditLog] ([OccurredAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_AuditLog_StaffId] ON [AuditLog] ([StaffId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_BloodBag_BloodType_Status_CollectedAtUtc] ON [BloodBag] ([BloodType], [Status], [CollectedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_BloodBag_CollectedAtUtc] ON [BloodBag] ([CollectedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_BloodBag_DonationReceiptId] ON [BloodBag] ([DonationReceiptId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_BloodBag_ExpiresOn_BloodType] ON [BloodBag] ([ExpiresOn], [BloodType]) WHERE [Status] = ''Available''');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_BloodBag_IssueReceiptId] ON [BloodBag] ([IssueReceiptId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Donor_BloodType] ON [Donor] ([BloodType]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Donor_Gender] ON [Donor] ([Gender]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Donor_LastDonated] ON [Donor] ([LastDonated]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Donor_Name] ON [Donor] ([Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Patient_BloodType] ON [Patient] ([BloodType]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Patient_Gender] ON [Patient] ([Gender]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Patient_Name] ON [Patient] ([Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Receipt_AppointmentId] ON [Receipt] ([AppointmentId]) WHERE [AppointmentId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Receipt_BloodType_CreatedAtUtc] ON [Receipt] ([BloodType], [CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Receipt_CreatedAtUtc] ON [Receipt] ([CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Receipt_Kind_CreatedAtUtc] ON [Receipt] ([Kind], [CreatedAtUtc]) INCLUDE ([BloodType], [Units]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Receipt_RequestId] ON [Receipt] ([RequestId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Receipt_StaffId] ON [Receipt] ([StaffId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_RefreshToken_ExpiresAtUtc] ON [RefreshToken] ([ExpiresAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_RefreshToken_FamilyId] ON [RefreshToken] ([FamilyId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_RefreshToken_StaffId] ON [RefreshToken] ([StaffId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RefreshToken_TokenHash] ON [RefreshToken] ([TokenHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Request_BloodType_Status_PlacedAtUtc] ON [Request] ([BloodType], [Status], [PlacedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Request_PatientId] ON [Request] ([PatientId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Request_PlacedAtUtc] ON [Request] ([PlacedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Request_RequiredBy] ON [Request] ([RequiredBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Request_Status_PlacedAtUtc] ON [Request] ([Status], [PlacedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Request_Status_RequiredBy] ON [Request] ([Status], [RequiredBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Staff_DisplayName] ON [Staff] ([DisplayName]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Staff_Username] ON [Staff] ([Username]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118212125_InitialSchema'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261118212125_InitialSchema', N'11.0.0');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    ALTER TABLE [Staff] DROP CONSTRAINT [CK_Staff_PasswordHash];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    DROP INDEX [IX_Request_PatientId] ON [Request];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    DROP INDEX [IX_Patient_BloodType] ON [Patient];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    DROP INDEX [IX_Donor_BloodType] ON [Donor];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    DROP INDEX [IX_Appointment_DonorId] ON [Appointment];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    DROP INDEX [IX_Appointment_PlacedAtUtc] ON [Appointment];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    DROP INDEX [IX_Appointment_SlotStartUtc] ON [Appointment];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    ALTER TABLE [Appointment] ADD [DonorBloodType] varchar(5) NOT NULL DEFAULT '';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    EXEC(N'UPDATE a SET a.DonorBloodType = d.BloodType FROM Appointment a JOIN Donor d ON d.Id = a.DonorId')
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    EXEC(N'ALTER TABLE [Staff] ADD CONSTRAINT [CK_Staff_PasswordHash] CHECK (LEN([PasswordHash]) BETWEEN 84 AND 256 AND LEN([PasswordHash]) % 4 = 0 AND [PasswordHash] NOT LIKE ''%[^A-Za-z0-9+/=]%'' COLLATE Latin1_General_100_BIN2 AND [PasswordHash] NOT LIKE ''%=%[^=]%'' COLLATE Latin1_General_100_BIN2 AND [PasswordHash] NOT LIKE ''%===%'' COLLATE Latin1_General_100_BIN2 AND [PasswordHash] LIKE ''AQAAAAIA____AAAAE%'' COLLATE Latin1_General_100_BIN2)');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    CREATE INDEX [IX_Request_BloodType_PlacedAtUtc] ON [Request] ([BloodType], [PlacedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    CREATE INDEX [IX_Request_BloodType_RequiredBy] ON [Request] ([BloodType], [RequiredBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    CREATE INDEX [IX_Request_PatientId_PlacedAtUtc] ON [Request] ([PatientId], [PlacedAtUtc]) INCLUDE ([RequiredBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    CREATE INDEX [IX_Patient_BloodType_Name] ON [Patient] ([BloodType], [Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    CREATE INDEX [IX_Donor_BloodType_Name] ON [Donor] ([BloodType], [Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    CREATE INDEX [IX_Appointment_DonorBloodType_PlacedAtUtc] ON [Appointment] ([DonorBloodType], [PlacedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    CREATE INDEX [IX_Appointment_DonorBloodType_SlotStartUtc] ON [Appointment] ([DonorBloodType], [SlotStartUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    CREATE INDEX [IX_Appointment_DonorId_SlotStartUtc] ON [Appointment] ([DonorId], [SlotStartUtc]) INCLUDE ([PlacedAtUtc], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    CREATE INDEX [IX_Appointment_PlacedAtUtc] ON [Appointment] ([PlacedAtUtc]) INCLUDE ([SlotStartUtc], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    CREATE INDEX [IX_Appointment_SlotStartUtc] ON [Appointment] ([SlotStartUtc]) INCLUDE ([PlacedAtUtc], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    CREATE INDEX [IX_Appointment_Status_PlacedAtUtc] ON [Appointment] ([Status], [PlacedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    EXEC(N'ALTER TABLE [Appointment] ADD CONSTRAINT [CK_Appointment_DonorBloodType] CHECK ([DonorBloodType] IN (''APos'', ''ANeg'', ''BPos'', ''BNeg'', ''ABPos'', ''ABNeg'', ''OPos'', ''ONeg''))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118220716_HardenHashCheckAndListIndexes'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261118220716_HardenHashCheckAndListIndexes', N'11.0.0');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Request_BloodType_PlacedAtUtc] ON [Request];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Request_BloodType_RequiredBy] ON [Request];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Request_BloodType_Status_PlacedAtUtc] ON [Request];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Request_PatientId_PlacedAtUtc] ON [Request];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Request_PlacedAtUtc] ON [Request];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Request_RequiredBy] ON [Request];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Request_Status_PlacedAtUtc] ON [Request];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Request_Status_RequiredBy] ON [Request];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Receipt_AppointmentId] ON [Receipt];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Receipt_BloodType_CreatedAtUtc] ON [Receipt];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Receipt_CreatedAtUtc] ON [Receipt];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Receipt_Kind_CreatedAtUtc] ON [Receipt];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Receipt_RequestId] ON [Receipt];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Patient_BloodType_Name] ON [Patient];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Patient_Name] ON [Patient];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Donor_BloodType_Name] ON [Donor];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Donor_LastDonated] ON [Donor];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Donor_Name] ON [Donor];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Appointment_DonorBloodType_PlacedAtUtc] ON [Appointment];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Appointment_DonorBloodType_SlotStartUtc] ON [Appointment];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Appointment_PlacedAtUtc] ON [Appointment];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Appointment_SlotStartUtc] ON [Appointment];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Appointment_Status_PlacedAtUtc] ON [Appointment];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    DROP INDEX [IX_Appointment_Status_SlotStartUtc] ON [Appointment];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    ALTER TABLE [Donor] ADD [MedicalHold] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Request_BloodType_PlacedAtUtc] ON [Request] ([BloodType], [PlacedAtUtc]) INCLUDE ([PatientId], [Units], [RequiredBy], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Request_BloodType_RequiredBy] ON [Request] ([BloodType], [RequiredBy]) INCLUDE ([PatientId], [Units], [PlacedAtUtc], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Request_BloodType_Status_PlacedAtUtc] ON [Request] ([BloodType], [Status], [PlacedAtUtc]) INCLUDE ([PatientId], [Units], [RequiredBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Request_BloodType_Status_RequiredBy] ON [Request] ([BloodType], [Status], [RequiredBy]) INCLUDE ([PatientId], [Units], [PlacedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Request_PatientId] ON [Request] ([PatientId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Request_PatientId_PlacedAtUtc] ON [Request] ([PatientId], [PlacedAtUtc]) INCLUDE ([RequiredBy], [Status], [BloodType], [Units]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Request_PlacedAtUtc] ON [Request] ([PlacedAtUtc]) INCLUDE ([PatientId], [BloodType], [Units], [RequiredBy], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Request_RequiredBy] ON [Request] ([RequiredBy]) INCLUDE ([PatientId], [BloodType], [Units], [PlacedAtUtc], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Request_Status_PlacedAtUtc] ON [Request] ([Status], [PlacedAtUtc]) INCLUDE ([PatientId], [BloodType], [Units], [RequiredBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Request_Status_RequiredBy] ON [Request] ([Status], [RequiredBy]) INCLUDE ([PatientId], [BloodType], [Units], [PlacedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Receipt_AppointmentId] ON [Receipt] ([AppointmentId]) INCLUDE ([CreatedAtUtc], [Kind], [BloodType]) WHERE [AppointmentId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Receipt_BloodType_CreatedAtUtc] ON [Receipt] ([BloodType], [CreatedAtUtc]) INCLUDE ([Kind], [Units], [StaffId], [AppointmentId], [RequestId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Receipt_CreatedAtUtc] ON [Receipt] ([CreatedAtUtc]) INCLUDE ([Kind], [BloodType], [Units], [StaffId], [AppointmentId], [RequestId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Receipt_Kind_BloodType_CreatedAtUtc] ON [Receipt] ([Kind], [BloodType], [CreatedAtUtc]) INCLUDE ([Units], [StaffId], [AppointmentId], [RequestId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Receipt_Kind_CreatedAtUtc] ON [Receipt] ([Kind], [CreatedAtUtc]) INCLUDE ([BloodType], [Units], [StaffId], [AppointmentId], [RequestId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Receipt_RequestId] ON [Receipt] ([RequestId]) INCLUDE ([CreatedAtUtc], [Kind], [BloodType]) WHERE [RequestId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Receipt_StaffId_CreatedAtUtc] ON [Receipt] ([StaffId], [CreatedAtUtc]) INCLUDE ([Kind], [BloodType]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Patient_BloodType_Name] ON [Patient] ([BloodType], [Name]) INCLUDE ([DateOfBirth], [Gender], [Phone]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Patient_Name] ON [Patient] ([Name]) INCLUDE ([DateOfBirth], [Gender], [BloodType], [Phone]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Patient_Phone] ON [Patient] ([Phone]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Donor_BloodType_LastDonated] ON [Donor] ([BloodType], [LastDonated]) INCLUDE ([Name], [DateOfBirth], [Gender], [Phone], [Frequency], [MedicalHold]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Donor_BloodType_Name] ON [Donor] ([BloodType], [Name]) INCLUDE ([DateOfBirth], [Gender], [Phone], [Frequency], [LastDonated], [MedicalHold]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Donor_LastDonated] ON [Donor] ([LastDonated]) INCLUDE ([Name], [DateOfBirth], [Gender], [BloodType], [Phone], [Frequency], [MedicalHold]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Donor_Name] ON [Donor] ([Name]) INCLUDE ([DateOfBirth], [Gender], [BloodType], [Phone], [Frequency], [LastDonated], [MedicalHold]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Donor_Phone] ON [Donor] ([Phone]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Appointment_DonorBloodType_PlacedAtUtc] ON [Appointment] ([DonorBloodType], [PlacedAtUtc]) INCLUDE ([SlotStartUtc], [Status], [DonorId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Appointment_DonorBloodType_SlotStartUtc] ON [Appointment] ([DonorBloodType], [SlotStartUtc]) INCLUDE ([PlacedAtUtc], [Status], [DonorId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Appointment_DonorId] ON [Appointment] ([DonorId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Appointment_PlacedAtUtc] ON [Appointment] ([PlacedAtUtc]) INCLUDE ([SlotStartUtc], [Status], [DonorId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Appointment_SlotStartUtc] ON [Appointment] ([SlotStartUtc]) INCLUDE ([PlacedAtUtc], [Status], [DonorId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Appointment_Status_DonorBloodType_PlacedAtUtc] ON [Appointment] ([Status], [DonorBloodType], [PlacedAtUtc]) INCLUDE ([SlotStartUtc], [DonorId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Appointment_Status_DonorBloodType_SlotStartUtc] ON [Appointment] ([Status], [DonorBloodType], [SlotStartUtc]) INCLUDE ([PlacedAtUtc], [DonorId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Appointment_Status_PlacedAtUtc] ON [Appointment] ([Status], [PlacedAtUtc]) INCLUDE ([SlotStartUtc], [DonorId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    CREATE INDEX [IX_Appointment_Status_SlotStartUtc] ON [Appointment] ([Status], [SlotStartUtc]) INCLUDE ([PlacedAtUtc], [DonorId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118234152_ListIndexesAndMedicalHold'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261118234152_ListIndexesAndMedicalHold', N'11.0.0');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119120000_StaffMustChangePassword'
)
BEGIN
    ALTER TABLE [Staff] ADD [MustChangePassword] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119120000_StaffMustChangePassword'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261119120000_StaffMustChangePassword', N'11.0.0');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119130000_TombstonesAndStaffCreatedAt'
)
BEGIN
    ALTER TABLE [Staff] ADD [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME());
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119130000_TombstonesAndStaffCreatedAt'
)
BEGIN
    CREATE TABLE [DeletedRecord] (
        [Id] int NOT NULL IDENTITY,
        [Entity] varchar(30) NOT NULL,
        [EntityId] int NOT NULL,
        [Json] nvarchar(max) NOT NULL,
        [DeletedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_DeletedRecord] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119130000_TombstonesAndStaffCreatedAt'
)
BEGIN
    CREATE INDEX [IX_DeletedRecord_DeletedAtUtc] ON [DeletedRecord] ([DeletedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119130000_TombstonesAndStaffCreatedAt'
)
BEGIN
    CREATE INDEX [IX_DeletedRecord_Entity_EntityId] ON [DeletedRecord] ([Entity], [EntityId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119130000_TombstonesAndStaffCreatedAt'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261119130000_TombstonesAndStaffCreatedAt', N'11.0.0');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119140000_WriteIssueFifoAndDemoIndexes'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Staff_IsDemo] ON [Staff] ([IsDemo]) INCLUDE ([DisplayName]) WHERE [IsDemo] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119140000_WriteIssueFifoAndDemoIndexes'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_BloodBag_BloodType_ExpiresOn] ON [BloodBag] ([BloodType], [ExpiresOn]) INCLUDE ([CollectedAtUtc]) WHERE [Status] = ''Available''');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119140000_WriteIssueFifoAndDemoIndexes'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261119140000_WriteIssueFifoAndDemoIndexes', N'11.0.0');
END;

COMMIT;
GO

