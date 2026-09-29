# Category lists

One domain per line (hosts-file style `0.0.0.0 domain` lines are accepted too; `#` comments are ignored).
The MSI ships these files to `%ProgramData%\Guardian\lists\` and the service loads them on first start.
"Refresh lists" on the Settings page re-downloads each list from the source URL in `sources.txt`
and replaces the stored copy; `custom.txt` (the parent's own list) is never overwritten.

Shipped lists: `adult.txt`, `gambling.txt`, `drugs.txt`, `weapons.txt`. They are small seed lists;
refresh them from a maintained source (the default sources are the StevenBlack hosts sub-lists,
which are MIT-licensed) after install.

A match creates an alert. Guardian never blocks anything.
