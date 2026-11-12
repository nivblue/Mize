*Niv Blumenfeld - Mize reservation controller assinment

**Assumptions i made:
- Because this is low scale project i asume right to invest less in recovery of the window timeframe in the rate-limitter - meaning i asumed that the rate limitter meant to reduce presure and load and so that in case of a restart there is no need to implement a recovery for the window timeframe and that is enough to restat it from zero (meaning if a supplier has passed the limit and needs to wait for the 60s to pass and in the middle the service crushed or restarted, the window is restarted).

- Because the requirements stated only "how many reservations were ingested vs throttled" and not a specific format, i assumed i have a free hand in that and let claude choose his own format, which there i also assumed that duplicated reservations (same reservation already ingested) should not be counted as ingested, i assumed that because the requirement said "don't write it again" - which i understood as - don't ingest.
- There were no requirements regarding how to run the code, as i didn't have c# cli tools installed on my local machine and didn't wont to waste time on asking claude to install it, while making it easier to run on other machines, i asked claude and made it all run - build, test, run using docker-compose - meaning all built, tested and run using docker containers to minimize dependability on my machine.
- In term of compromises there were no one truth in the requirement, so in my head i set that data reliable is number one, afterwards that should work as fast as possible, which guided me through the decision claude asked me to choose.

**Claude suggestions that i disagreed or changed:
- Claude assumed using .db file as the db, and keep the integration experience with sqllite in this exercies to a minimum, i fell it necessary to run an sqllite container to make more close to the intented result as i understood from the requirements.
- Claude offered several times a deeper reservation details validation such as limit Id size, case sensetive checks, timezones etc'. some of them i accepted, but other seems that they could wait for later as there were no strict definition for reservation validations, so i took the minimum level was accepted to me at this point.
- Claude offered a change to the stats return body, i decline because the current body return whats asked for, and if want more there is a third object containing more data, so the receiver can read on the higher level what he asked for, and if wanted, on the deeper level he will have the rest.
- Claude suggested several times a different packages, i wanted to minimize use of additional packages to reduce complexity and dependability - because it is a low scale project.


** What would i do if i had more time?
- Scalability - some assumptions and decision made in this project relayed on the fact that this is a low scale project, such as in-memory cache, low validation and load-balancer, meaning:
 * I would scale up the service to have more then one instance and distribute the reservations between them using a load-balancer which distribute by supplierId.
 * I would use reddis cache instead of in-memory cache which will fast enough but also more reliable in case the service instance crushes, which will keep performances in about the same level while giving us more consistancy in term of restart.
- Proper logger - a logger which prints to file - easy bug investigations.
- Add more aggresive validtions
- Add swagger file for easier understading of the endpoint and request body

