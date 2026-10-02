-- 天车配置表：新增三轴偏移量（坐标标定/微调）
-- 说明：本项目的表结构为手工维护（EF 迁移仅作参考），结构变更用本目录下的 SQL 脚本执行。
-- 幂等提示：MySQL 的 ADD COLUMN 不支持 IF NOT EXISTS，重复执行会报“列已存在”，可忽略。
-- 已于 2026-09-19 应用到 automated_line 库。

ALTER TABLE `CraneConfig` ADD COLUMN `OffsetX` DOUBLE NOT NULL DEFAULT 0;
ALTER TABLE `CraneConfig` ADD COLUMN `OffsetY` DOUBLE NOT NULL DEFAULT 0;
ALTER TABLE `CraneConfig` ADD COLUMN `OffsetZ` DOUBLE NOT NULL DEFAULT 0;
