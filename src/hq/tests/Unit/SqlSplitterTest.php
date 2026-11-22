<?php

use App\Support\Schema\SqlSplitter;

it('splits on semicolons outside quotes and comments', function () {
    $sql = <<<'SQL'
        -- header comment; with a semicolon
        CREATE TABLE a (id INT, note VARCHAR(10) DEFAULT 'x;y');   -- trailing; comment
        /* block; comment */
        INSERT INTO a VALUES (1, 'it''s; fine'), (2, "dq; \" quote");
        # hash comment;
        UPDATE `weird;name` SET note = 'a\'b;' ;
        SQL;

    expect(SqlSplitter::split($sql))->toBe([
        "CREATE TABLE a (id INT, note VARCHAR(10) DEFAULT 'x;y')",
        "INSERT INTO a VALUES (1, 'it''s; fine'), (2, \"dq; \\\" quote\")",
        "UPDATE `weird;name` SET note = 'a\'b;'",
    ]);
});

it('supports DELIMITER blocks for triggers', function () {
    $sql = "SELECT 1;\nDELIMITER $$\nCREATE TRIGGER t BEFORE INSERT ON a FOR EACH ROW BEGIN SET NEW.id = 1; END$$\nDELIMITER ;\nSELECT 2;";

    expect(SqlSplitter::split($sql))->toBe([
        'SELECT 1',
        'CREATE TRIGGER t BEFORE INSERT ON a FOR EACH ROW BEGIN SET NEW.id = 1; END',
        'SELECT 2',
    ]);
});

it('drops empty and comment-only parts', function () {
    expect(SqlSplitter::split("-- only a comment\n;;  \n/* x */"))->toBe([]);
    expect(SqlSplitter::split('SELECT 1'))->toBe(['SELECT 1']);
});

it('splits every shared migration into statements', function () {
    foreach (glob(__DIR__.'/../../../../database/migrations/*.sql') as $file) {
        $statements = SqlSplitter::split((string) file_get_contents($file));
        expect($statements)->not->toBeEmpty();
        foreach ($statements as $statement) {
            expect(str_starts_with(ltrim($statement), '--'))->toBeFalse();
        }
    }
});
