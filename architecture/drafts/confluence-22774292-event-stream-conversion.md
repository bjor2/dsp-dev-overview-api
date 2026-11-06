# CustomerProject: Convert to PICo / Table Event streams

Source: https://dahl-it.atlassian.net/wiki/spaces/DSPDOC/pages/22774292 · version 13 · opened 2026-11-06 (Europe/Oslo). Historical design, not deployment proof; markdown macro conversion lossy.

# Suggested reading

General information about the CustomerProject service: [Customer Project](https://dahl-it.atlassian.net/wiki/spaces/DSPDOC/pages/22779513/Customer+Project)

Principles used for conversion:[DSP Platform Ingress Design Document](https://dahl-it.atlassian.net/wiki/spaces/DSPDOC/pages/22783423/DSP+Platform+Ingress+Design+Document)

Common technical fields are explained here: [PICo driven Domain Services Guideline](https://dahl-it.atlassian.net/wiki/spaces/DSPDOC/pages/22774028/PICo+driven+Domain+Services+Guideline+Service+Transition+Guideline) and the plan we are going for is [Plan B](https://dahl-optimera.atlassian.net/wiki/spaces/DSPDOC/pages/162640101414#%E2%80%9CPlan-B%E2%80%9D---definition).

# Purpose

To enable PICo / Table Event streams in the CustomerProject service, we want to convert the current ADF based CSV ingress with the set of required PICo event streams from OnPrem Optimera.

This design document is only focusing on replacing the ingress data from the Optimera M3 instance, not the Dahl M3 instance

# Streaming data flow architectual overview

# M3Cloud data ingestion

There are two ADF jobs that needs to be replaced by PICO ingress, used to populate ProjectWithPrice and CustomerAddress domain models.

## ProjectWithPrice

```sql
-- Customers with active project prices
select
  UYCUNO, 
  UYADID, 
  MAX(LEAST(UYLVDT, QFGIDT)) AS UYLVDT
from @{pipeline().globalParameters.Optimera_M3_Schema}.OAGRHE
inner join @{pipeline().globalParameters.Optimera_M3_Schema}.OOQUOH QF 
    on QFCONO = UYCONO 
      and QFORNO = UYTX40 
      and QFORCD IN ('002', '300')
inner join @{pipeline().globalParameters.Optimera_M3_Schema}.OOHEAD 
    on OACONO = UYCONO 
    and OACUNO = UYCUNO 
    and OAADID = UYADID 
    and OAORNO = UYTX40 
    and OAORTP IN ('010', '012')
where UYCONO = 100
  and UYAGST = 20
  and UYSTDT <= INTEGER(CURRENT DATE)
  and UYLVDT >= INTEGER(CURRENT DATE)
  and QFGIDT >= INTEGER(CURRENT DATE)
group by UYCUNO, UYADID
FOR READ ONLY
```

```sql
-- Customers with active project prices
SELECT
  UY.Customer                         AS CustomerId, 
  UY.AddressNumber                    AS ErpAddressId, 
  MAX(LEAST(UY.ValidTo, QF.ValidTo))  AS ValidToDate
  
FROM CustomerAgreementHead AS UY

INNER JOIN QuotationOrderHead AS QF 
	ON QF.Company = UY.Company 
	  AND QF.CustomerOrderNumber = UY.Description 
	  AND QF.QuotationReason IN ('002', '300')
	  
INNER JOIN HeaderFile AS OA
	ON OA.Company = UY.Company 
	AND OA.Customer = UY.Customer 
	AND OA.AddressNumber = UY.AddressNumber 
	AND OA.CustomerOrderNumber = UY.Description 
	AND OA.CustomerOrderType IN ('010', '012')
	
WHERE UY.Company = 100
  AND UY.Status = 20
  AND UY.StartDate <= INTEGER(CURRENT DATE)
  AND UY.ValidTo >= INTEGER(CURRENT DATE)
  AND QF.ValidTo >= INTEGER(CURRENT DATE)

GROUP BY UY.Customer, UY.AddressNumber
```

The old ADF import process sequence:

1. ADF query includes all rows, not a delta. Every row is upserted in the DB
    1.  **Subtask**: ADF query doesn’t include rows with `StartDate` in the future. Make sure this is still handled
2. After file import, the service calls `BatchDeleteOlderThanTimestamp(...)`, which hard deletes all `ProjectWithPrice` rows with `ValidToDate` older than yesterday. 
    1. `CustomerAddress?.ProjectWithPrice?.ValidToDate >= DateTime.UtcNow.Date` is the only place `ProjectWithPrice` is used. Since `ValidToDate` is used here, **there’s no need to delete old rows**
3. Then `_storageWriter.GetProjectsByProjectPriceLastUpdatedSince(...)` is called which finds all `ProjectWithPrice`rows with `LastModifiedUtc`larger than the start of the update process. For each of these, publish `CustomerProjectChanged` PCO
    1.  **Subtask**: Publish `CustomerProjectChanged` PCO after actual change to `ProjectWithPrice`. Ensure receiver handles potential duplicates

### M3 to ProjectWithPrice mapping

| **M3 Table** | **M3 Column** | **PICO** | **PICO Property** | **ProjectWithPrice property** |
| --- | --- | --- | --- | --- |
| `OOHEAD` | `UYCONO` | CustomerAgreementHead | Customer | CustomerId |
| `OOHEAD` | `UYADID` | CustomerAgreementHead | AddressNumber | ErpAddressId |
| `OOHEAD` | `UYLVDT` | CustomerAgreementHead | ValidTo | ValidToDate |
| `OOQUOH` | `QFGIDT` | QuotationOrderHead | ValidTo | ValidToDate |


## CustomerAddress

```sql
with CHANGED (CONO, CUNO, ADID) as (
  select DISTINCT OPCONO as CONO, OPCUNO as CUNO, OPADID as ADID
  from @{pipeline().globalParameters.Optimera_M3_Schema}.CEMAIL
  left join @{pipeline().globalParameters.Optimera_M3_Schema}.OCUSAD 
  on OPCONO = CBCONO and OPCUNO = CBEMKY
  where CBCONO = 100
    and CBEMTP = '01'
    and @{activity('LookupLastWaterMark').output.firstRow.LastWatermark1} > 0 -- Handled by other selections if watermark is zero.
    and 
      CBLMDT >= 
        INTEGER(
          DATE(
            TIMESTAMP('1970-01-01-00.00.00.000000') 
              + (@{activity('LookupLastWaterMark').output.firstRow.LastWatermark1}/1000) SECONDS
          )
        )

  union

  select DISTINCT OPCONO as CONO, OPCUNO as CUNO, OPADID as ADID
  from @{pipeline().globalParameters.Optimera_M3_Schema}.OCUSMA
  left join @{pipeline().globalParameters.Optimera_M3_Schema}.OCUSAD 
    on OPCONO = OKCONO and OPCUNO = OKCUNO
  where OKCONO = 100
    and @{activity('LookupLastWaterMark').output.firstRow.LastWatermark1} 
      > 0 -- Handled by other selections if watermark is zero.
    and OKLMTS > @{activity('LookupLastWaterMark').output.firstRow.LastWatermark1}
    and OKLMTS <= @{variables('NewWatermark')}

  union

  select DISTINCT OPCONO as CONO, OPCUNO as CUNO, OPADID as ADID
  from @{pipeline().globalParameters.Optimera_M3_Schema}.OCUSAD
  where OPCONO = 100
    AND (
             @{activity('LookupLastWaterMark').output.firstRow.LastWatermark1} = 0 
          OR OPLMTS > @{activity('LookupLastWaterMark').output.firstRow.LastWatermark1}
        )
    AND OPLMTS <= @{variables('NewWatermark')}
)

select 
  OPCUNO, 
  OPADRT, 
  OPCUA1, 
  OPCUA2, 
  OPADID, 
  OPPONO, 
  OPTOWN, 
  OPFVDT, 
  OPLVDT, 
  OPLMTS, 
  OPPHNO, 
  OPYREF, 
  OPADCG, 
  OPCSCD, 
  OKCUNM, 
  OKPHNO, 
  CBEMAL, 
  OKPHN2
from CHANGED CH
  left join @{pipeline().globalParameters.Optimera_M3_Schema}.OCUSAD 
    on OPCONO = CONO 
      and OPCUNO = CUNO 
      and OPADID = ADID
  left join @{pipeline().globalParameters.Optimera_M3_Schema}.OCUSMA 
    on OKCONO = OPCONO 
      and OKCUNO = OPCUNO
  left join @{pipeline().globalParameters.Optimera_M3_Schema}.CEMAIL 
    on CBCONO = OKCONO 
      and CBEMTP = '01' 
      and CBEMKY = OKCUNO

where OPADRT in (1,3)
  and OPADID <> ''
  and not OPCUNO like (' %')
  and not OPCUNO like ('%''%')
  and not OPCUNO like ('%,%')

FOR READ ONLY
```

```sql
WITH CHANGED (Company, Customer, AddressNumber) AS (

  --Has CustomerEmail (EmailType = '01') changed since last run and LastWatermark1 is set
  SELECT DISTINCT OP.Company AS Company, OP.Customer AS Customer, OP.AddressNumber AS AddressNumber
  FROM CustomerEmail AS CB
  --Seems wrong that this is LEFT JOIN as the SELECT-statement only contains values from OP
  LEFT JOIN CustomerAddress AS OP ON OP.Company = CB.Company AND OP.Customer = CB.EmailKeyValue 
  WHERE CB.Company = 100
    AND CB.EmailType = '01'
    AND @LastWatermark1 > 0 -- Handled by other selections if watermark is zero.
    
	--Changed since last time (delta)
	AND CB.ChangeDate >= 
		INTEGER(
			DATE(
					TIMESTAMP('1970-01-01-00.00.00.000000') + (@LastWatermark1/1000) SECONDS
				)
		)

  UNION

  --Has Customer changed since last run and LastWatermark1 is set
  SELECT DISTINCT OP.Company AS Company, OP.Customer AS Customer, OP.AddressNumber AS AddressNumber
  FROM Customer AS OK 
  --Seems wrong that this is LEFT JOIN as the SELECT-statement only contains values from OP
  LEFT JOIN CustomerAddress AS OP ON OP.Company = OK.Company AND OP.Customer = OK.Customer
  WHERE OK.Company = 100
  
	--LastWatermark1 is set and between LastWatermark1 and UtcNow
    AND @LastWatermark1 > 0 -- Handled by other selections if watermark is zero.
    AND OK.[Timestamp] > @LastWatermark1
    AND OK.[Timestamp] <= @UtcNow

  UNION

  --Has CustomerAddress changed since last run
  SELECT DISTINCT OP.Company AS Company, OP.Customer AS Customer, OP.AddressNumber AS AddressNumber
  FROM CustomerAddress AS OP
  WHERE OP.Company = 100
	--LastWatermark is NOT set or between LastWatermark1 and UtcNow
    AND (@LastWatermark1 = 0 OR OP.[Timestamp] > @LastWatermark1)
    AND OP.[Timestamp] <= @UtcNow
)



--Select from Changed rows
--left join CustomerAddress with AddressType in (1,3) AND AddressNumber <> '' AND Customer LIKE ...
--Left join Customer 
--left join CustomerEmail with EmailType = '01'
SELECT 
  OP.Customer               AS CustomerId, 
  OP.AddressType            AS AddressType, 
  OP.AddressLine1           AS Address1, 
  OP.AddressLine2           AS Address2, 
  OP.AddressNumber          AS ErpAddressId, 
  OP.PostalCode             AS Zip, 
  OP.City                   AS City, 
  OP.ValidFrom              AS ValidFromDate, 
  OP.ValidTo                AS ValidToDate, 
  OP.TelephoneNumber1       AS Phone, 
  OP.YourReference1         AS YourReference, 
  OP.AddressCategory        AS ReferenceId, 
  OP.Country                AS Country, 
  OK.Name                   AS CustomerName, 
  CB.ElectronicMailAddress  AS CustomerEmail, 
  OK.TelephoneNumber2       AS CustomerPhone
FROM CHANGED CH
  LEFT JOIN CustomerAddress AS OP ON OP.Company = Company AND OP.Customer = Customer AND OP.AddressNumber = AddressNumber
  LEFT JOIN Customer AS OK ON OK.Company = OP.Company AND OK.Customer = OP.Customer
  LEFT JOIN CustomerEmail AS CB ON CB.Company = OK.Company AND CB.EmailType = '01' AND CB.EmailKeyValue = OK.Customer

WHERE OP.AddressType in (1,3)
  AND OP.AddressNumber <> ''
  AND NOT OP.Customer LIKE (' %')
  AND NOT OP.Customer LIKE ('%''%')
  AND NOT OP.Customer LIKE ('%,%')
```

The old ADF import process sequence:

1. ADF query includes customers + email + address that has changed since last run
    1. This is naturally built-in the ingress streaming
2. After ADF import. the service calls `_storageWriter.GetNewAddressesByCreatedDate(...)` which returns newly created `CustomerAddresses` with `CreatedDateUtc > start of import timestamp`. For each of those rows, `CreateProjectsFromErpReferences` command is sent to queue
    1.  **Subtask**: Send `CreateProjectsFromErpReferences`command when a new address is created. Ensure receiver handles potential duplicates
3. Then `_storageWriter.GetModifiedAddressIdsByLastModifiedDate(...)` is called which returns `CustomerAddresses` with `CreatedDateUtc != LastModifiedUtc && LastModifiedUtc > start of import timestamp`. For each of those rows, `HandleCustomerAddressChanged` command is sent to queue.
    1.  **Subtask**: Send `HandleCustomerAddressChanged` command when an address is changed. Ensure receiver handles potential duplicates

### M3 to CustomerAddress mapping

| **M3 Table** | **M3 Column** | **PICO** | **PICO Property** | **CustomerAddress property** |
| --- | --- | --- | --- | --- |
| `OCUSAD` | `OPCUNO` | CustomerAddress | Customer | CustomerId |
| `OCUSAD` | `OPADRT` | CustomerAddress | AddressType | AddressType |
| `OCUSAD` | `OPCUA1` | CustomerAddress | AddressLine1 | Address1 |
| `OCUSAD` | `OPCUA2` | CustomerAddress | AddressLine2 | Address2 |
| `OCUSAD` | `OPADID` | CustomerAddress | AddressNumber | ErpAddressId |
| `OCUSAD` | `OPPONO` | CustomerAddress | PostalCode | Zip |
| `OCUSAD` | `OPTOWN` | CustomerAddress | City | City |
| `OCUSAD` | `OPFVDT` | CustomerAddress | ValidFrom | ValidFromDate |
| `OCUSAD` | `OPLVDT` | CustomerAddress | ValidTo | ValidToDate |
| `OCUSAD` | `OPPHNO` | CustomerAddress | TelephoneNumber1 | Phone |
| `OCUSAD` | `OPYREF` | CustomerAddress | YourReference1 | YourReference |
| `OCUSAD` | `OPADCG` | CustomerAddress | AddressCategory | ReferenceId |
| `OCUSAD` | `OPCSCD` | CustomerAddress | Country | Country |
| `OCUSMA` | `OKCUNM` | Customer | Name | CustomerName |
| `OCUSMA` | `OKPHN2` | Customer | TelephoneNumber2 | CustomerPhone |
| `CEMAIL` | `CBEMAL` | CustomerEmail | ElectronicMailAddress | CustomerEmail |
