-- Отпечаток вложения: один и тот же файл не заливается на сервер дважды
-- (миграция 20).
--
-- Отправить одно видео двум собеседникам значило залить его на сервер два
-- раза — второй раз ровно так же долго, как первый, хотя те же байты там уже
-- лежат. Отпечаток позволяет узнать СВОЁ уже залитое вложение и скопировать
-- его внутри базы: по сети не уходит ни байта, отправка мгновенная.
--
-- Приложения делают это сами (DbMigrator, миграция 20). Файл нужен только
-- если у учётной записи приложения нет права ALTER — тогда столбцы кладёт
-- администратор. До тех пор всё работает по-старому: код проверяет наличие
-- столбца и молча откатывается на обычную заливку.

ALTER TABLE messages        ADD COLUMN file_sha CHAR(64) NULL;
ALTER TABLE group_messages  ADD COLUMN file_sha CHAR(64) NULL;
ALTER TABLE server_messages ADD COLUMN file_sha CHAR(64) NULL;

-- Донора ищем только среди своих вложений, поэтому индекс по паре.
CREATE INDEX idx_file_sha ON messages        (sender_id, file_sha);
CREATE INDEX idx_file_sha ON group_messages  (sender_id, file_sha);
CREATE INDEX idx_file_sha ON server_messages (sender_id, file_sha);
