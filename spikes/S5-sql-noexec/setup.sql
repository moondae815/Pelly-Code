-- S5: 권한 조합별 로그인·사용자 (PellyCorpus)
USE master;
IF SUSER_ID('pelly_view') IS NULL CREATE LOGIN pelly_view WITH PASSWORD = 'Pelly_View_2026!', CHECK_POLICY = OFF;
IF SUSER_ID('pelly_showplan') IS NULL CREATE LOGIN pelly_showplan WITH PASSWORD = 'Pelly_Plan_2026!', CHECK_POLICY = OFF;
IF SUSER_ID('pelly_alter') IS NULL CREATE LOGIN pelly_alter WITH PASSWORD = 'Pelly_Alter_2026!', CHECK_POLICY = OFF;
GO
USE PellyCorpus;
IF USER_ID('pelly_view') IS NULL CREATE USER pelly_view FOR LOGIN pelly_view;
IF USER_ID('pelly_showplan') IS NULL CREATE USER pelly_showplan FOR LOGIN pelly_showplan;
IF USER_ID('pelly_alter') IS NULL CREATE USER pelly_alter FOR LOGIN pelly_alter;
GO
-- ① VIEW DEFINITION만
GRANT VIEW DEFINITION TO pelly_view;
-- ② ① + SHOWPLAN
GRANT VIEW DEFINITION TO pelly_showplan;
GRANT SHOWPLAN TO pelly_showplan;
-- ③ ① + 스키마 ALTER
GRANT VIEW DEFINITION TO pelly_alter;
GRANT ALTER ON SCHEMA::dbo TO pelly_alter;
GO
-- ④ 권한 없음 (public 역할만) — VIEW DEFINITION 필요성 확인용
USE master;
IF SUSER_ID('pelly_none') IS NULL CREATE LOGIN pelly_none WITH PASSWORD = 'Pelly_None_2026!', CHECK_POLICY = OFF;
GO
USE PellyCorpus;
IF USER_ID('pelly_none') IS NULL CREATE USER pelly_none FOR LOGIN pelly_none;
GO
