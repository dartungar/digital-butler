# Code Review Action Plan - Digital Butler

**Created:** December 30, 2025  
**Updated:** December 30, 2025  
**Based on:** CODE_REVIEW.md  
**Context:** Personal single-user application (security de-prioritized)

This document outlines specific fixes for each issue identified in the code review, organized by priority.

---

## Phase 1: High Priority Bug Fixes ✅ COMPLETED

### 1.1 Reuse TelegramBotClient in Scheduler ✅

**File:** `DigitalButler.Web/Program.cs`, `DigitalButler.Web/Scheduler.cs`

**Fix Applied:** TelegramBotClient is now registered as a singleton in `Program.cs` and injected into `SchedulerService` instead of being recreated each tick.

---

### 1.2 Fix Cron/Interval Scheduling ✅

**File:** `DigitalButler.Web/Scheduler.cs`

**Fix Applied:** Added `ParseIntervalMinutes()` method to properly parse the `CronOrInterval` field. Simple integer values are treated as minute intervals; values starting with "@" or "*/N" patterns are parsed accordingly.

---

### 1.3 Fix Context Updater Registration ✅

**Files:** `DigitalButler.Context/ContextUpdaterRegistry.cs` (NEW), `DigitalButler.Web/Program.cs`, `DigitalButler.Web/Scheduler.cs`, `DigitalButler.Web/ManualSyncRunner.cs`

**Fix Applied:** Created `IContextUpdaterRegistry` interface and implementation to allow lookup of context updaters by source. Scheduler and ManualSyncRunner now use the registry instead of generic `IContextUpdater` service resolution.

---

## Phase 2: Medium Priority - Code Quality ✅ COMPLETED

### 2.1 Extract Shared Time Window Logic ✅

**New File:** `DigitalButler.Context/TimeWindowHelper.cs`

**Fix Applied:** Created `TimeWindowHelper` static class with `GetDailyWindow()` and `GetWeeklyWindow()` methods. Updated both `BotService.cs` and `Scheduler.cs` to use this shared helper.

---

### 2.2 Log TimeZone Fallback Warning ✅

**File:** `DigitalButler.Context/TimeZoneService.cs`

**Fix Applied:** Added `ILogger<TimeZoneService>` dependency and logging when falling back to UTC due to invalid timezone configuration.

---

### 2.3 Consolidate OpenAiEndpoint Logic ✅

**Files:** `DigitalButler.Context/SkillRouting.cs`, `DigitalButler.Context/OpenAiSummarizationService.cs`

**Fix Applied:** Made `OpenAiEndpoint` class public. Removed ~110 lines of duplicate `ResolveEndpoint()`, `ExtractResponsesText()`, and `TryExtractText()` methods from `OpenAiSummarizationService`, now using the shared class.

---

### 2.4 Remove Static Mutex from ManualSyncRunner ✅

**File:** `DigitalButler.Web/ManualSyncRunner.cs`

**Fix Applied:** Changed `static readonly SemaphoreSlim Mutex` to instance-based `readonly SemaphoreSlim _mutex`.

---

## Phase 3: Low Priority - Nice to Have ✅ COMPLETED

### 3.1 Add Database Indices ✅

**File:** `DigitalButler.Context/Data/ButlerSchemaInitializer.cs`

**Fix Applied:** Added indices for `RelevantDate` and `IsTimeless` columns on `ContextItems` table.

---

### 3.2 Enable Connection Pooling ✅

**File:** `DigitalButler.Context/Data/ButlerDb.cs`

**Fix Applied:** Connection string now automatically appends `;Pooling=True` if not already present.

---

### 3.3 Delete Unused Files ❌ SKIPPED

**Reason:** The `Class1.cs` files are **not unused** - they contain essential type definitions (`ContextItem`, `ContextSource`, `IContextUpdater`, etc.). The file naming is misleading but the code is required.

---

### 3.4 Add Health Check Endpoint ✅

**File:** `DigitalButler.Web/Program.cs`

**Fix Applied:** Added `builder.Services.AddHealthChecks()` and `app.MapHealthChecks("/health").AllowAnonymous()`.

---

### 3.5 Add AI Debug Logging ✅

**File:** `DigitalButler.Context/OpenAiSummarizationService.cs`

**Fix Applied:** Added debug logging at start of `SummarizeAsync` with task name, model, source count, and item count.

---

## Summary

| Phase | Focus | Items | Status |
|-------|-------|-------|--------|
| 1 | Bug Fixes | 3 | ✅ Complete |
| 2 | Code Quality | 4 | ✅ Complete |
| 3 | Nice to Have | 5 | ✅ Complete (1 skipped) |

**All actionable items completed.** Only 3.3 was skipped because the files contain required code despite misleading names.
