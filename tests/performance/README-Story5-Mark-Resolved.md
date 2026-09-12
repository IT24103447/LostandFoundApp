# Story 5 JMeter execution

Open `Story5-Mark-Resolved.jmx` in JMeter and replace its five variables with the local Item Service URL, two current `auth_token` values, and disposable ACTIVE report IDs.

The owner sampler expects `200` and permanently resolves `LOST_ITEM_ID`. The non-owner sampler expects `403` and must leave `FOUND_ITEM_ID` unchanged. Do not reuse either ID for another run without resetting its database status. Save result files under `tests/performance/results/`; that local evidence folder is ignored by Git.
