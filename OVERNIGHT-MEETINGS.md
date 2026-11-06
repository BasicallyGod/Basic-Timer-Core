# Overnight meetings (0.10.0)

A meeting keeps its calendar start date and a start time within that date. Its end is an offset from the same date, up to 24 hours after the start. For example, 23:00 to the following 02:00 is stored as At = 23 hours, End = 26 hours. Recurrence templates use the same representation.

Data format 7 reads existing HH:mm values and adds d.HH:mm for an end on the following date. Existing records are not rewritten. The normal upgrade saves a pre-upgrade backup, and an older library refuses format 7. Return to the backup before using an older app; a package rollback is not a data-format downgrade.
