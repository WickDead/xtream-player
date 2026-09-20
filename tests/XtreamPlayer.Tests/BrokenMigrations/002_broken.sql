-- Test fixture: creates a table, then fails. Proves the whole script rolls back
-- as one unit rather than leaving half_built behind.
CREATE TABLE half_built (id INTEGER PRIMARY KEY);
THIS IS NOT VALID SQL;
