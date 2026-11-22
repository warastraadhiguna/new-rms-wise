<?php

namespace App\Support\Printing;

use App\Support\Decimal;
use Brick\Math\RoundingMode;
use InvalidArgumentException;

/**
 * PHP port of Pos.Shared.Printing.ReceiptFormatter and ReceiptTexts (the node's sale receipt), used by the HQ settings
 * page to preview receipts exactly as the cashier prints them. Both implementations are checked against the same
 * fixture (docs/fixtures/receipt-format.json): change them together.
 *
 * Data (decimal strings, times already in the branch timezone):
 *   header, footer: list<string>; number, sold_at_local ("Y-m-d H:i"), cashier; customer ?string;
 *   items: list<{name, qty, unit, unit_price, line_total}>; subtotal, discount_amount, discount_pct ?string,
 *   rounding, total; payments: list<{method, is_cash, is_credit, amount, tendered ?string, change ?string}>;
 *   due_date ?string ("Y-m-d").
 */
final class ReceiptFormatter
{
    public const WIDTH_80MM = 48;

    public const WIDTH_58MM = 32;

    public const MIN_WIDTH = 24;

    public const DEFAULT_FOOTER = 'Terimakasih Atas Kunjungannya';

    public const PHONE_PREFIX = 'Telp. ';

    private const MAX_NAME_LINES = 2;

    private const FIELD_LABEL_WIDTH = 7;

    /**
     * @param  array<string, mixed>  $data
     * @return list<string>
     */
    public static function format(array $data, int $width = self::WIDTH_80MM, bool $reprint = false): array
    {
        if ($width < self::MIN_WIDTH) {
            throw new InvalidArgumentException('A receipt needs at least '.self::MIN_WIDTH.' characters per line.');
        }
        $rule = str_repeat('-', $width);
        $lines = [];

        foreach (self::strings($data['header'] ?? []) as $header) {
            array_push($lines, ...self::centered($header, $width));
        }
        if ($reprint) {
            array_push($lines, ...self::centered('CETAK ULANG', $width));
        }

        $lines[] = $rule;
        $lines[] = self::field('No', (string) $data['number'], $width);
        $lines[] = self::field('Tanggal', self::date((string) $data['sold_at_local'], 'd/m/Y H:i'), $width);
        $lines[] = self::field('Kasir', (string) $data['cashier'], $width);
        $customer = $data['customer'] ?? null;
        if (is_string($customer) && trim($customer) !== '') {
            $lines[] = self::field('Member', $customer, $width);
        }
        $lines[] = $rule;

        /** @var list<array<string, mixed>> $items */
        $items = $data['items'] ?? [];
        foreach ($items as $item) {
            array_push($lines, ...array_slice(self::wrap((string) $item['name'], $width), 0, self::MAX_NAME_LINES));
            array_push($lines, ...self::itemDetail($item, $width));
        }
        $lines[] = $rule;

        array_push($lines, ...self::row('SubTotal', self::number((string) $data['subtotal']), $width));
        $discount = (string) ($data['discount_amount'] ?? '0');
        if (Decimal::compare($discount, '0') !== 0) {
            $pct = $data['discount_pct'] ?? null;
            $label = is_string($pct) && Decimal::compare($pct, '0') !== 0 ? 'Diskon '.self::number($pct).'%' : 'Potongan';
            array_push($lines, ...self::row($label, self::number($discount), $width));
        }
        $rounding = (string) ($data['rounding'] ?? '0');
        if (Decimal::compare($rounding, '0') !== 0) {
            array_push($lines, ...self::row('Pembulatan', self::number($rounding), $width));
        }
        array_push($lines, ...self::row('Grand Total', self::number((string) $data['total']), $width));

        /** @var list<array<string, mixed>> $payments */
        $payments = $data['payments'] ?? [];
        $credit = array_values(array_filter($payments, fn (array $p) => (bool) $p['is_credit']));
        if ($credit !== []) {
            $names = implode(' + ', array_values(array_unique(array_map(fn (array $p) => (string) $p['method'], $credit))));
            array_push($lines, ...self::row('Cara Bayar', $names, $width));
            $cash = array_filter($payments, fn (array $p) => ! (bool) $p['is_credit']);
            array_push($lines, ...self::row('Uang Muka', self::number(self::sum(array_map(fn (array $p) => (string) $p['amount'], $cash))), $width));
            array_push($lines, ...self::row('Sisa Piutang', self::number(self::sum(array_map(fn (array $p) => (string) $p['amount'], $credit))), $width));
        } else {
            $methods = $payments === [] ? '-' : implode(' + ', array_map(fn (array $p) => (string) $p['method'], $payments));
            array_push($lines, ...self::row('Cara Bayar', $methods, $width));
            array_push($lines, ...self::row('Bayar', self::number(self::sum(array_map(fn (array $p) => (string) ($p['tendered'] ?? $p['amount']), $payments))), $width));
            array_push($lines, ...self::row('Kembali', self::number(self::sum(array_map(fn (array $p) => (string) ($p['change'] ?? '0'), $payments))), $width));
        }
        $due = $data['due_date'] ?? null;
        if (is_string($due) && $due !== '') {
            array_push($lines, ...self::row('Jatuh Tempo', self::date($due, 'd/m/Y'), $width));
        }
        $lines[] = $rule;

        foreach (self::strings($data['footer'] ?? []) as $footer) {
            array_push($lines, ...self::centered($footer, $width));
        }

        return $lines;
    }

    /**
     * Header lines from the receipt settings; empty fields are not printed (ReceiptTexts.Header).
     *
     * @return list<string>
     */
    public static function header(?string $storeName, ?string $address, ?string $city, ?string $phone): array
    {
        return self::nonEmpty([$storeName, $address, $city, $phone === null || trim($phone) === '' ? null : self::PHONE_PREFIX.trim($phone)]);
    }

    /** @return list<string> */
    public static function footer(?string $footer1, ?string $footer2): array
    {
        return self::nonEmpty([$footer1, $footer2]);
    }

    /** Indonesian number like .NET "#,0.####" in id-ID: "1.250.000", "0,5", "-50". */
    public static function number(string $value): string
    {
        $decimal = Decimal::of($value)->toScale(4, RoundingMode::HalfUp)->strippedOfTrailingZeros();
        $negative = $decimal->isNegative();
        $text = (string) $decimal->abs();
        [$int, $fraction] = array_pad(explode('.', $text, 2), 2, '');
        $int = strrev(implode('.', str_split(strrev($int), 3)));

        return ($negative ? '-' : '').$int.($fraction === '' ? '' : ','.$fraction);
    }

    /**
     * @param  array<string, mixed>  $item
     * @return list<string>
     */
    private static function itemDetail(array $item, int $width): array
    {
        $indent = '  ';
        $text = self::number((string) $item['qty']).' '.$item['unit'].' x '.self::number((string) $item['unit_price']);
        $detail = array_map(fn (string $l) => $indent.$l, self::wrap($text, $width - mb_strlen($indent)));
        $total = self::fit(self::number((string) $item['line_total']), $width);
        $last = count($detail) - 1;

        if (mb_strlen($detail[$last]) + 1 + mb_strlen($total) <= $width) {
            $detail[$last] .= mb_str_pad($total, $width - mb_strlen($detail[$last]), ' ', STR_PAD_LEFT);
        } else {
            $detail[] = mb_str_pad($total, $width, ' ', STR_PAD_LEFT);
        }

        return $detail;
    }

    /** @return list<string> */
    private static function row(string $label, string $value, int $width): array
    {
        if (mb_strlen($label) + 1 + mb_strlen($value) <= $width) {
            return [$label.mb_str_pad($value, $width - mb_strlen($label), ' ', STR_PAD_LEFT)];
        }

        return [self::fit($label, $width), mb_str_pad(self::fit($value, $width), $width, ' ', STR_PAD_LEFT)];
    }

    private static function field(string $label, string $value, int $width): string
    {
        return self::fit(mb_str_pad($label, self::FIELD_LABEL_WIDTH).': '.$value, $width);
    }

    /** @return list<string> */
    private static function centered(string $text, int $width): array
    {
        return array_map(fn (string $l) => str_repeat(' ', intdiv($width - mb_strlen($l), 2)).$l, self::wrap($text, $width));
    }

    /**
     * Greedy word wrap; words longer than the line are cut. Blank text gives one empty line.
     *
     * @return non-empty-list<string>
     */
    private static function wrap(string $text, int $width): array
    {
        $lines = [];
        $current = '';
        foreach (array_filter(explode(' ', $text), fn (string $w) => $w !== '') as $word) {
            $rest = $word;
            while ($rest !== '') {
                $needed = $current === '' ? mb_strlen($rest) : mb_strlen($current) + 1 + mb_strlen($rest);
                if ($needed <= $width) {
                    $current .= ($current === '' ? '' : ' ').$rest;
                    $rest = '';
                } elseif ($current !== '') {
                    $lines[] = $current;
                    $current = '';
                } else {
                    $lines[] = mb_substr($rest, 0, $width);
                    $rest = mb_substr($rest, $width);
                }
            }
        }
        if ($current !== '' || $lines === []) {
            $lines[] = $current;
        }

        return $lines;
    }

    /** "2026-11-22 14:05" → "22/11/2026 14:05" (the value is already branch-local); unparsable text as it is. */
    private static function date(string $value, string $format): string
    {
        $date = date_create_immutable($value);

        return $date === false ? $value : $date->format($format);
    }

    private static function fit(string $text, int $width): string
    {
        return mb_strlen($text) <= $width ? $text : mb_substr($text, 0, $width);
    }

    /** @param  list<string>|array<int, string>  $values */
    private static function sum(array $values): string
    {
        return (string) Decimal::add(...array_values($values));
    }

    /**
     * @param  list<?string>  $values
     * @return list<string>
     */
    private static function nonEmpty(array $values): array
    {
        return array_values(array_map(fn (?string $v) => trim((string) $v), array_filter($values, fn (?string $v) => $v !== null && trim($v) !== '')));
    }

    /** @return list<string> */
    private static function strings(mixed $values): array
    {
        return is_array($values) ? array_values(array_map(fn (mixed $v) => (string) $v, $values)) : [];
    }
}
