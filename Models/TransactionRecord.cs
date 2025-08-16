using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FuelTransactionImporterService.Models
{
    public class TransactionRecord
    {
        public string Plate { get; set; }
        public decimal Liter { get; set; }
        public DateTime TransactionDate { get; set; }
        public string FuelType { get; set; }
        public string StationTransactionId { get; set; }
        public string ViuId { get; set; }
        public decimal TotalPrice { get; set; }
        public decimal UnitPrice { get; set; }
    }
}
