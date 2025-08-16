using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FuelTransactionImporterService.Models
{
    public class ApiTransaction
    {
        public string PLATE { get; set; }
        public DateTime PUMP_TRNX_TIME { get; set; }
        public decimal QUANTITY { get; set; }
        public string PRODUCT_NAME { get; set; }
        public string DRIVER { get; set; }
        public string STATION_TRNX_ID { get; set; }
        public string VIU_ID { get; set; }
        public decimal AMOUNT { get; set; }
        public decimal UNIT_PRICE { get; set; }
    }

}
