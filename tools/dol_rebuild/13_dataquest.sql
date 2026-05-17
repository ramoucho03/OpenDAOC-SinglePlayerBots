-- dataquest: full reimport from DOLSharp (8 rows).
-- Wipe is done in 01_wipe.sql; this file just bulk-inserts.

SET FOREIGN_KEY_CHECKS=0;

INSERT IGNORE INTO `dataquest` (`Name`, `StartType`, `StartName`, `StartRegionID`, `AcceptText`, `Description`, `SourceName`, `SourceText`, `StepType`, `StepText`, `StepItemTemplates`, `AdvanceText`, `TargetName`, `TargetText`, `CollectItemTemplate`, `MaxCount`, `MinLevel`, `MaxLevel`, `RewardMoney`, `RewardXP`, `RewardCLXP`, `RewardRP`, `RewardBP`, `OptionalRewardItemTemplates`, `FinalRewardItemTemplates`, `FinishText`, `QuestDependency`, `AllowedClasses`, `ClassType`, `LastTimeRowUpdated`) VALUES
  ('Otherworld Obelisk', 4, 'Obelisk', 197, '', '', '', '', '', '', '', '', '', '', '', 1, 1, 50, '0', '0', '0', '0', '0', '', '', '', '', '', '', '2000-01-01 00:00:00'),
  ('Abandoned Mines Obelisk', 4, 'Obelisk', 228, '', '', '', '', '', '', '', '', '', '', '', 1, 1, 50, '0', '0', '0', '0', '0', '', '', '', '', '', '', '2000-01-01 00:00:00'),
  ('Glashtin Forge Obelisk', 4, 'Obelisk', 99, '', '', '', '', '', '', '', '', '', '', '', 1, 1, 50, '0', '0', '0', '0', '0', '', '', '', '', '', '', '2000-01-01 00:00:00'),
  ('Underground Forest Obelisk', 4, 'Obelisk', 96, '', '', '', '', '', '', '', '', '', '', '', 1, 1, 50, '0', '0', '0', '0', '0', '', '', '', '', '', '', '2000-01-01 00:00:00'),
  ('Queen''s Labyrinth Obelisk', 4, 'Obelisk', 94, '', '', '', '', '', '', '', '', '', '', '', 1, 1, 50, '0', '0', '0', '0', '0', '', '', '', '', '', '', '2000-01-01 00:00:00'),
  ('Hibernia''s Deadlands Obelisk', 4, 'Obelisk', 97, '', '', '', '', '', '', '', '', '', '', '', 1, 1, 50, '0', '0', '0', '0', '0', '', '', '', '', '', '', '2000-01-01 00:00:00'),
  ('Hibernia''s Frontlines Obelisk', 4, 'Obelisk', 95, '', '', '', '', '', '', '', '', '', '', '', 1, 1, 50, '0', '0', '0', '0', '0', '', '', '', '', '', '', '2000-01-01 00:00:00'),
  ('Underground Forest Obelisk', 4, 'Obelisk', 66, '', '', '', '', '', '', '', '', '', '', '', 1, 1, 50, '0', '0', '0', '0', '0', '', '', '', '', '', '', '2000-01-01 00:00:00');

SET FOREIGN_KEY_CHECKS=1;
