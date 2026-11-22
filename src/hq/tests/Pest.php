<?php

use Tests\TestCase;

/*
| Feature tests boot Laravel and use pos_hq_test (see Tests\TestCase); Unit tests are plain PHP (no app, no DB).
*/
pest()->extend(TestCase::class)->in('Feature');
