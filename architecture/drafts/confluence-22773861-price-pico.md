# Price: Convert to PICO / Table Event streams (id 22773861 v67)
Source: https://dahl-it.atlassian.net/wiki/spaces/DSPDOC/pages/22773861

# Purpose

The purpose is to enable PICO event streams to the Price service, replacing the ADF based CSV ingress. The first step is to replace the ingress from the existing OnPrem M3 instances, and after use the same process for M3 Cloud. 


# High level architectual overview


# Saving PICO events

We’ll introduce an Ingress storage to keep the PICO events. In Price this storage will be the same SQL Server instance as the one that holds domain data, but all tables should be put in the *Ingress* database schema to keep them separated.

Only the minimum required data will be stored in the ingress storage in order to create the domain models. Keeping only a minimum makes it easier to compare for changes and will save database storage space as the PICOs can be very large. The full PICOs will be stored in the Event point outside Price and can be resent from there for rehydration purposes.


# Single PICO event can trigger multiple domain model updates

Receiving a PICO event can end up updating a single or multiple domain models based on it.

A example is where a PICO is used across multiple models, for example the SalePriceListChanged PICO event from Optimera M3, where a single event might trigger updates of all these different domain models: 

- Campaign for Optimera
- CampaignPrice for Optimera
- PriceList for Optimera
- PriceListTableCampaign for Optimera

In this case, the PICO will result in multiple update commands being sent to into the domain. One per domain model type. 

Another example is a change to a M3 table which affects many instances on the same domain model type, for example that a single change to a CustomerAgreementHead for Optimera might affect many OfferPrice models. In this case it will be a single command going from Ingress to Domain, but the domain update will update multiple domain models.


# Detailed data flow

When receiving a single PICO it will be saved to Ingress storage and 0, 1 or multiple commands will be sent to the domain, one per domain model type and data source combination. On command handling, the domain will read the necessary data from the Ingress storage and update 0 to multiple domain models of the given type, depending on the type of change. 


After domain models are saved or updated based on PICO events, the rest of the service should be able to serve the same data as before with no additional changes.

Sequence diagram of the flow with more details


## Out of order messages

An incoming PICO is discarded if its DataVersion is less than the DataVersion of the PICO saved in Ingress storage with the same Id. 

Domain models are not updated if the contents haven’t changed.


## Concurrent messages

If two message handlers are about to update the same domain model in parallel, one of them should fail and be automatically retried. A ConcurrencyVersion field will be introduced in all domain model tables and optimistic concurrency control on that field will be used to ensure the object hasn’t been updated by anyone else since it was fetched from the database. We’ll use the ConcurrencyCheck attribute from Entity framework to achieve this (<https://learn.microsoft.com/en-us/ef/core/saving/concurrency?tabs=data-annotations>)


## Idempotency on automatic retries

The PICO handlers does two separate write operations

1. Saving ingress data to the database
2. Sending commands to queue

These operations are not transactional, so we could risk that the saving succeeds, but the command sending fails or only partiallly suceeds. In that case an error is thrown in the PICO event handler which will result in a retry. The system have to ensure that the outcome of the retry is the same as on the original run, meaning checking that the DB is up to date and resending the commands. 

This might lead to duplicate outgoing commands, but that is taken care of in the command handlers.

If the Price service is to publish PCO’s in the future, the same mechanism needs to be implemented there.


## Deleted source data

When data is deleted in M3, PICO event with `deleted = true` is emitted. When receiving a PICO for deleted data, we should soft delete the PICO by setting `IsDeleted = true` in the Ingress storage. 

When a command to update the domain model is handled and at least one of the required ingress models has `IsDeleted=true`, the domain model should be hard deleted or kept, duplicating how it’s done by the existing domain logic. 

Current logic is that Price is loading all data from M3 every night. The `replaceAll` argument of the `IImportStrategy.Import(...)` implementations decides if deleted data should be deleted or kept. The current implementation is:

- **PriceCostStrategy **sets `replaceAll=false` which means data **is kept **in Price
- **All others **uses `replaceAll=true` which means data **is hard deleted **from Price


# DataSource when introducing PICO streaming

DataSource in the Price service is defined like this

```c#
public enum DataSource
{
    Dahl = 1,
    Optimera = 2,
    Flisekompaniet = 3
}
```

Where Dahl is for data originating from the Dahl M3 instance, whilst both Optimera and Flisekompaniet originates from the Optimera M3 instance, populated by different ADF queries. 

All domain models belongs one, and only one, of these data sources. 

When consuming PICO streams for the on prem M3 instances, a couple of new data sources are introduced

```c#
    M3Cloud = 4,
    OptimeraOnPrem = 5
```

M3Cloud is populated from M3Cloud PICO streams which will replace both the Optimera and Flisekompaniet data sources. 

The purpose of OptimeraOnPrem is that it can be populated from PICO streams at the same time as the old blob ingress from ADF is still running. Then it’s possible to compare data source 2+3 with 5 and it should contain the exact same data. This makes it possible to find data discrepancies. 

# PCOs

There are currently no PCOs being published from Price service


# M3 queries table usage (Optimera and FK as of 12.06.2024)

The table below is created based on the ADF queries. It shows the mapping between the Price domain entities and the M3 tables for the DataSource.Optimera and DataSource.Flisekompaniet.  The implementation strateigy column shows which strategy is used to populate the domain entity. DataSource.Dahl is included only for reference. This is not implemented

| **DataSource** | **Domain Entity** | **M3 Tables** | **Queries** | **Implementation strategy** |
| --- | --- | --- | --- | --- |
| Optimera | Campaign | `OPRBAS = SalePriceList CSYTAB = SystemTable OASITN = Assortment MITLOC = BalanceIdentity MITWHL = Warehouse` | `e-- Campaigns (Campaigns.csv) with OPTIMERA_WAREHOUSES (WHLO) as ( select MWWHLO from @{pipeline().globalParameters.Optimera_M3_Schema}.MITWHL where MWCONO = 100   and MWDIVI = '001'   and MWWHTY <> 'ZZ' ), OUTLET_ITEMS (CONO, WHLO, ITNO) as (   select distinct MLCONO, MLWHLO, MLITNO   from @{pipeline().globalParameters.Optimera_M3_Schema}.MITLOC   where MLCONO = 100     and MLWHSL = 'OUTLET'     and (MLSTQT-MLALQT) > 0     and exists (select 1 from OPTIMERA_WAREHOUSES where WHLO = MLWHLO) ), OUTLET_PRICELIST (ODPRRF, ODITNO, ODFVDT, ODLVDT, ODSAPR, ODCMNO) as (   select ODPRRF, ODITNO, ODFVDT, ODLVDT, ODSAPR, ODCMNO   from @{pipeline().globalParameters.Optimera_M3_Schema}.OPRBAS   where ODCONO = 100     and ODPRRF = 'MNSS'     and ODFVDT <= INTEGER(CURRENT DATE)     and ODLVDT >= INTEGER(CURRENT DATE) )  -- Regular campaigns select ODCMNO as FZPIDE, CTTX15 as FZTX15, CTTX40 as FZTX40, '' as FZCUSE, MAX(ODFVDT) as FZFVDT, MAX(ODLVDT) as FZLVDT, '' as FZOTYG from @{pipeline().globalParameters.Optimera_M3_Schema}.OPRBAS   left join @{pipeline().globalParameters.Optimera_M3_Schema}.CSYTAB on CTCONO = ODCONO and CTSTCO = 'CMNO' and CTSTKY = ODCMNO where ODCONO = 100   and ODITNO in (     select OIITNO as ITNO     from @{pipeline().globalParameters.Optimera_M3_Schema}.OASITN     where OICONO = 100       and OIASCD = 'MDC'   )   and ODPRRF in ('ML','MLN','MM','MN','MP','NH','NI','NM','NN','NS','NT','NV','NW','NØ','PD','PE','PF','PG','PH','PI','PJ','PL','PM','PN','PO','PP','PQ','PR','PS','PT','PU','PV','PW','PX','PY','QA','QB','QC','QE','QF','QH','QI','QJ','QK','QL','QM','QN','QQ','QS','QT','QU','QV','QW','QX','QY','QZ','QÆ','QØ','RB','RD','RE','RF','RG','RH','RI','RJ','RL','RM','RN','RP','RQ','SB','SC','SD','SF','SG','SH','SI','SJ','SL','SM','SN','SO','SP','SQ','SR','SS','SSTORD','ST','SU','SV','SW','SX','SY','SÅ','SÆ','SØ','TA','TB','TC','TD','TE','TF','TG','TH','TI','TJ','TK','TL','TM','WA','WB','WC','WD','WE','WF','WH','WI','ZZ')   and ODFVDT <= INTEGER(CURRENT DATE)   and ODLVDT >= INTEGER(CURRENT DATE)   and ODCMNO != '    ' group by ODCMNO, CTTX15, CTTX40  union  -- Outlet campaign select 'Outlet' as FZPIDE, 'Outlet' as FZTX15, 'Outlet' as FZTX40, '' as FZCUSE, MIN(ODFVDT) as FZFVDT, MAX(ODLVDT) as FZLVDT, '' as FZOTYG from (   select ODFVDT, ODLVDT,     ROW_NUMBER() over (partition by ODPRRF, ODITNO order by ODFVDT desc) as RN   from OUTLET_PRICELIST   where exists (select 1 from OUTLET_ITEMS where ITNO = ODITNO) ) where rn = 1  union  -- Customer Club campaign select 'CustomerClub' as FZPIDE, 'CustomerClub' as FZTX15, 'CustomerClub' as FZTX40, '' as FZCUSE, MIN(ODFVDT) as FZFVDT, MAX(ODLVDT) as FZLVDT, '' as FZOTYG from (   select ODFVDT, ODLVDT,     ROW_NUMBER() over (partition by ODPRRF, ODITNO order by ODFVDT desc) as RN   from @{pipeline().globalParameters.Optimera_M3_Schema}.OPRBAS   where ODCONO = 100     and ODPRRF = 'MK'     and ODITNO in (select OIITNO from @{pipeline().globalParameters.Optimera_M3_Schema}.OASITN where OICONO = 100 and OIASCD = 'MDC')     and ODFVDT <= INTEGER(CURRENT DATE)     and ODLVDT >= INTEGER(CURRENT DATE) ) where rn = 1  /for read only``;WITH  CAMPAIGNPRICELISTS AS (     -- Rule: No warehouse     SELECT SPM.PriceList     FROM SalePriceMatrixes AS SPM     WHERE SPM.DataSource = @DataSource  	    AND SPM.PriceListTable = 'N-MONTERX' 	    AND SPM.PriceList NOT IN ('N13', 'N13N', 'N13H', 'NMK', 'NMNSS')         AND SPM.StartValue2 = '' 	    AND SPM.IsDeleted = 0      UNION      -- Rule: Warehouse     SELECT SPM.PriceList      FROM SalePriceMatrixes AS SPM     WHERE SPM.DataSource = @DataSource  	    AND SPM.PriceListTable = 'N-MONTERX' 	    AND SPM.PriceList NOT IN ('N13', 'N13N', 'N13H', 'NMK', 'NMNSS')         AND SPM.StartValue2 IN ( 		    SELECT WarehouseId  		    FROM Warehouses AS WH 		    WHERE WH.DataSource = @DataSource                  AND WH.WarehouseType <> 'ZZ'                  AND WH.IsDeleted = 0 	    )          AND SPM.IsDeleted = 0 ),  OPTIMERA_WAREHOUSES (Warehouse) AS ( 	SELECT WH.WarehouseId 	FROM Warehouses AS WH 	WHERE WH.DataSource = @DataSource 	    AND WH.WarehouseType <> 'ZZ' 	    AND WH.IsDeleted = 0 ),  OUTLET_ITEMS (DataSource, Warehouse, ItemNumber) AS (     SELECT DISTINCT BID.DataSource, BID.Warehouse, BID.ItemNumber     FROM BalanceIdentities AS BID      WHERE BID.DataSource = @DataSource     AND BID.Location = 'OUTLET'     AND (BID.OnhandBalanceApproved - BID.AllocatedQuantityBasicUM) > 0     AND EXISTS (SELECT 1 FROM OPTIMERA_WAREHOUSES WHERE Warehouse = BID.Warehouse) 	AND BID.IsDeleted = 0 ),  OUTLET_PRICELIST (DataSource, PriceList, ItemNumber, ValidFrom, ValidTo, SalesPrice, SalesCampaign) AS (     SELECT SPL.DataSource, SPL.PriceList, SPL.ItemNumber, SPL.ValidFrom, SPL.ValidTo, SPL.SalesPrice, SPL.SalesCampaign     FROM SalePriceLists AS SPL     WHERE SPL.DataSource = @DataSource     AND SPL.PriceList = @OutletPriceList     AND SPL.ValidFrom <= SYSUTCDATETIME()     AND SPL.ValidTo >= SYSUTCDATETIME() 	AND SPL.IsDeleted = 0 )              --Regular campaigns SELECT      SPL.SalesCampaign       AS Id,      STF.Name                AS DescriptionShort,      STF.Description         AS Description,      ''                      AS CustomerSelection,      MAX(SPL.ValidFrom)      AS ValidFromDate,      MAX(SPL.ValidTo)        AS ValidToDate,      ''                      AS OrderTypeGroup  FROM SalePriceLists AS SPL     LEFT JO

… truncated for draft note …
