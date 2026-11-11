# claims-service

Mikrostoritev za zahtevke (ClaimsService, Zavarovalnica Horica). Stranka odda zahtevek, storitev ga shrani, objavi `claim.submitted.v1`, po predlogu agentov zahtevek samodejno odobri ali ga pošlje v ročni pregled (`review.requested.v1`), po odločitvi zaposlenega pa nastavi končno stanje. Hrani zgodovino obravnave vsakega zahtevka in je odgovorna za njegovo končno stanje.

.NET 10, EF Core + PostgreSQL, Kafka (CloudEvents, outbox), JWT (auth-service ali Keycloak).

## Zagon

    docker compose up --build

Swagger: http://localhost:5081/swagger (Postgres na 5434, Kafka na 9092). Tabele in teme se naredijo same.

Brez Dockerja za aplikacijo:

    docker compose up -d postgres kafka
    dotnet run --project src/ClaimsService

Brez Kafke: `Kafka__Enabled=false` (dogodki ostanejo v tabeli `outbox_messages` in se pošljejo, ko Kafko vklopiš).

Testi: `dotnet test`.

## Hiter preizkus

V Swaggerju (samo Development) ali v `src/ClaimsService/requests.http`:

1. `POST /claims` (zahtevek za 1200 €) → `SUBMITTED`
2. `POST /dev/claims/{id}/recommendation` (kot da je odgovoril AgentService) → `NEEDS_REVIEW`, objavi se `review.requested.v1`
3. `POST /dev/claims/{id}/review-completed` z `NeedsMoreInfo` → `NEEDS_INFO`
4. `POST /dev/claims/{id}/documents` → ponovna analiza (`SUBMITTED`, krog 2, spet `claim.submitted.v1`)
5. `GET /claims/{id}` in `GET /claims/{id}/history`

Zahtevek do 300 € z dokumenti in oceno tveganja do 0,20 se po 2. koraku odobri samodejno (`APPROVED`).

Endpointi `/dev/...` uporabljajo isto kodo kot Kafka consumer, zato se stanje spreminja enako kot v pravem sistemu. Zunaj Development vrnejo 404.

## Stanja

    SUBMITTED ──predlog, pravila ok──▶ APPROVED
    SUBMITTED ──predlog, sicer──▶ NEEDS_REVIEW ──pregled──▶ APPROVED | REJECTED | NEEDS_INFO
    NEEDS_INFO ──nov dokument──▶ SUBMITTED (ponovna analiza, UC-7)

`APPROVED` in `REJECTED` sta končni stanji; poznejši dogodki ju ne spremenijo.

**Samodejna odobritev** (samo ta storitev odloča o njej): predlog agentov je `APPROVE`, znesek ≤ 300 €, ocena tveganja ≤ 0,20, vsaj en dokument, nobena kontrola agentov ni `FAIL` ali `FLAG`, `policyValid` in `documentsComplete` nista `false`. Karkoli drugega (tudi predlog zavrnitve ali manjkajoča ocena) gre zaposlenemu. Meje so v `AutoApproval` (`MaxAmount`, `MaxRiskScore`, `RequireDocuments`, `Enabled`).

## API

Oblika odgovorov je enaka modelu `Claim` v web-ui (`src/core/models.ts`), zato UI lahko preklopi z mock podatkov brez sprememb.

- `POST /claims` (stranka) z `{ "policyNumber", "incidentDate": "yyyy-MM-dd", "amount", "description", "documents": ["ime.pdf"], "type"? }` → `201` in zahtevek
- `GET /claims` (filtri `status`, `policyNumber`, `customerId` samo za zaposlene, `limit`); stranka dobi le svoje, zaposleni vse; najnovejši prvi
- `GET /claims/{id}`, `GET /claims/{id}/history`; tuj zahtevek vrne 404
- `GET /claims/history` (vodja): vsi vnosi zgodovine vseh zahtevkov, najnovejši prvi (filtra `claimId`, `event`)
- `GET /health/live`, `GET /health/ready`

Stranka v zgodovini ne vidi notranjih vnosov (podatki agentov, ocena tveganja, razlogi za pregled).

Če je nastavljen `Policies__BaseUrl`, storitev ob oddaji pokliče PolicyService `GET /policies/{number}` z uporabnikovim žetonom: neznana polica ali polica druge stranke vrne 400, nedosegljiv PolicyService 503, vrsta zahtevka (`type`) pa se vzame iz police. Brez nastavitve se polica ne preverja ob oddaji (preverijo jo agenti).

## Dogodki

CloudEvents v JSON, ime teme je enako tipu dogodka, ključ je `claimId`. Spremembe zahtevka in dogodki se shranijo v isti transakciji (outbox), dostava je vsaj enkrat, zato naj prejemnik deduplicira po `id`.

**Objavlja:**

- `claim.submitted.v1`: `claimId`, `policyNumber`, `policyId`, `customerId`, `claimant`, `type`, `incidentDate`, `claimedAmount`, `currency`, `description`, `documents` (imena), `analysisRound`, `reanalysis`, `submittedAt`
- `review.requested.v1`: `claimId`, `policyId`, `customerId`, `claimedAmount`, `currency`, `riskScore`, `riskReasons` (skupaj z razlogi za ročni pregled), `reviewReasons`, `recommendation`, `summary`, `analysisRound` (oblika, ki jo bere review-service)
- `claim.status.changed.v1`: `claimId`, `customerId`, `status`, `previousStatus`, `reason`, `analysisRound`, `changedAt`

**Prejema** (sprejme ovojnico ali samo `data`; neberljiva sporočila gredo v `<tema>.dlq`):

- `claim.recommendation.created.v1` (AgentService): obvezen `claimId`; `recommendation` (`APPROVE`/`REJECT`/`REVIEW`), `summary`, `riskScore`, `riskReasons`, `agents` (`[{ agent, outcome: PASS|FLAG|FAIL, note }]`), `policyValid`, `documentsComplete`, `analysisRound`. Upošteva se le, ko je zahtevek `SUBMITTED`; predlog za star krog analize se zavrže.
- `review.completed.v1` (ReviewService): `claimId`, `decision` (`Approved`/`Rejected`/`NeedsMoreInfo`), `reason`, `reviewId`, `reviewerId`, `reviewerName`, `decidedAt`. Upošteva se le, ko je zahtevek `NEEDS_REVIEW`.
- `document.added.v1` (DocumentService): `claimId` in `name` (ali `fileName`), neobvezno `documentId`, `uploadedById`, `uploadedAt`; ali seznam `documents` / `names`.

Primeri so v `samples/`.

## Prijava

V Development je prijava izklopljena (`Auth__Mode=Disabled`, uporabnik `dev-user` z vlogama `CLIENT,ADMIN`). Drugega uporabnika simuliraš z glavami `X-Dev-User`, `X-Dev-Roles` in `X-Dev-Email` (v Swaggerju z gumbom Authorize).

Drugje je treba nastaviti `Auth__Authority` (npr. `http://auth-service:8081` ali URL Keycloak realma). Vloge se primerjajo ne glede na velike črke: stranka `CLIENT`, zaposleni `AGENT`, `REVIEWER`, `ADMIN`, vodja `ADMIN` (`Auth__CustomerRoles`, `Auth__StaffRoles`, `Auth__ManagerRoles`).

Za web-ui lokalno z auth-service: `Auth__Mode=Jwt`, `Auth__Authority=http://localhost:8081`, `Auth__RequireHttpsMetadata=false`.

## Kubernetes (AKS)

Push na `main` požene teste, zgradi sliko `horica-claims-service` in jo namesti v namespace `staging`; v `prod` gre z ročnim zagonom workflowa (Actions → CI/CD → Run workflow).

V namespaceu mora obstajati Secret `db-claims` s ključem `DB_CONNECTION_STRING` (Npgsql), npr.:

    kubectl -n staging create secret generic db-claims --from-literal=DB_CONNECTION_STRING="Host=<server>.postgres.database.azure.com;Port=5432;Database=claims_staging;Username=<user>;Password=<password>;Ssl Mode=Require"

Baza se ustvari ob prvem zagonu, če jo uporabnik sme ustvariti. Kafka je na `horica-kafka-kafka-bootstrap.kafka:9092`; teme imajo v stagingu in prodi predpono (`staging.claim.submitted.v1`, `prod.claim.submitted.v1` ...), ker si okolji delita eno Kafko. Brez Kafke storitev vseeno teče, dogodki počakajo v outbox tabeli.

## Najpomembnejše nastavitve

`ConnectionStrings__ClaimsDb`, `Kafka__BootstrapServers`, `Kafka__Enabled`, `Auth__Mode`, `Auth__Authority`, `Policies__BaseUrl`, `AutoApproval__MaxAmount`, `AutoApproval__MaxRiskScore`. Vse je v `appsettings.json`, v okolju se preglasi z `__` namesto `:`. Baza mora pripadati samo tej storitvi.
