Workflow extras packs go here as v2.json, v3.json, ...

Leave empty for the first catalog test.
The engine loads every v*.json. Next run ALTERs extra columns
on the five silver tables, then fills them.

Example v2.json:

{
  "version": 2,
  "columns": [
    {
      "table": "test_sessions",
      "name": "locale",
      "data_type": "STRING",
      "token": "@locale=",
      "grain": "session"
    }
  ]
}
