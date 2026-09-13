# Story 6 — View Item Details JMeter run

This read-only plan uses an authenticated active item. It verifies `200`, the configured public title, and absence of the `hiddenInformation` property. Kafka is not applicable to this story.

```powershell
jmeter -n -t .\tests\performance\Story6-View-Item-Details.jmx -l .\tests\performance\results\story6-item-details.jtl -Jitem_id="<active item UUID>" -Jauth_token="<auth_token>" -Jexpected_title="<exact item title>"
```

Record the aggregate report and confirm no 5xx responses. The initial local target is p95 below 500 ms; adjust if the project adopts a formal SLA.
