-- Друзья: личный список (направленный — "я добавил его").
-- Совместимо с MySQL. Безопасно запускать повторно.
CREATE TABLE IF NOT EXISTS friends (
    user_id    INT NOT NULL,
    friend_id  INT NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (user_id, friend_id)
);
