<?php

namespace App\Support\Schema;

/**
 * Splits a MySQL script into statements on ";" outside quotes, backticks and comments
 * ("-- ", "#" and block comments), like DbUp's MySQL splitter. DELIMITER lines are supported.
 */
final class SqlSplitter
{
    /**
     * @return list<string> statements without the delimiter; empty statements and comment-only parts are dropped
     */
    public static function split(string $sql): array
    {
        $statements = [];
        $current = '';
        $delimiter = ';';
        $length = strlen($sql);
        $i = 0;
        $lineStart = true;

        while ($i < $length) {
            // DELIMITER $$ at the start of a line changes the statement end.
            if ($lineStart && preg_match('/\G[ \t]*DELIMITER[ \t]+(\S+)[ \t]*(\r?\n|$)/Ai', $sql, $m, 0, $i)) {
                self::push($statements, $current);
                $current = '';
                $delimiter = $m[1];
                $i += strlen($m[0]);
                $lineStart = true;

                continue;
            }

            $char = $sql[$i];
            $next = $i + 1 < $length ? $sql[$i + 1] : '';

            if ($char === '-' && $next === '-' && ($i + 2 >= $length || ctype_space($sql[$i + 2]))) {
                $end = strpos($sql, "\n", $i);
                $i = $end === false ? $length : $end;

                continue;
            }
            if ($char === '#') {
                $end = strpos($sql, "\n", $i);
                $i = $end === false ? $length : $end;

                continue;
            }
            if ($char === '/' && $next === '*') {
                $end = strpos($sql, '*/', $i + 2);
                $i = $end === false ? $length : $end + 2;
                $current .= ' ';

                continue;
            }
            if ($char === "'" || $char === '"' || $char === '`') {
                $end = self::closingQuote($sql, $i, $char);
                $current .= substr($sql, $i, $end - $i + 1);
                $i = $end + 1;
                $lineStart = false;

                continue;
            }
            if (substr_compare($sql, $delimiter, $i, strlen($delimiter)) === 0) {
                self::push($statements, $current);
                $current = '';
                $i += strlen($delimiter);

                continue;
            }

            $current .= $char;
            $lineStart = $char === "\n";
            $i++;
        }

        self::push($statements, $current);

        return $statements;
    }

    /** Index of the closing quote; doubled quotes and backslash escapes (not in backticks) stay inside. */
    private static function closingQuote(string $sql, int $start, string $quote): int
    {
        $length = strlen($sql);
        for ($i = $start + 1; $i < $length; $i++) {
            if ($quote !== '`' && $sql[$i] === '\\') {
                $i++;

                continue;
            }
            if ($sql[$i] === $quote) {
                if ($i + 1 < $length && $sql[$i + 1] === $quote) {
                    $i++;

                    continue;
                }

                return $i;
            }
        }

        return $length - 1;
    }

    /** @param list<string> $statements */
    private static function push(array &$statements, string $statement): void
    {
        $statement = trim($statement);
        if ($statement !== '') {
            $statements[] = $statement;
        }
    }
}
