# Story 8 — My Reports JMeter run

Start the Auth and Item services. Obtain fresh owner and other-user `auth_token` values from authenticated browser cookies. Create one distinctive lost and one distinctive found report for the owner.

```powershell
jmeter -n -t .\tests\performance\Story8-My-Reports.jmx -l .\tests\performance\results\story8-my-reports.jtl -Jowner_token="<owner auth_token>" -Jother_token="<other auth_token>" -Jowner_lost_title="<owner lost title>" -Jowner_found_title="<owner found title>"
```

Expected: owner requests return their title; the other-user lost-history response does not contain the owner lost title. The result file is ignored by Git.
