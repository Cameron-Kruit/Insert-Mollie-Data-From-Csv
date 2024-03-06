#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mollie.Api.Client;
using Mollie.Api.Client.Abstract;
using Mollie.Api.Models;
using Mollie.Api.Models.Customer;
using Mollie.Api.Models.Mandate;
using Mollie.Api.Models.Subscription;

namespace MollieScript;

class Program {
    static async Task Main(string[] args) {
        using IHost host = Host.CreateDefaultBuilder(args).Build();
                var conf = host.Services.GetRequiredService<IConfiguration>();

        string? apiKey  = conf["ApiKey"];
        string? csvPath = conf["CsvPath"];
        if(apiKey == null || csvPath == null)
            throw new Exception("Configuration not in order");

        ICustomerClient     customerClient     = new CustomerClient    (apiKey, new HttpClient());
        IMandateClient      mandateClient      = new MandateClient     (apiKey, new HttpClient());
        ISubscriptionClient subscriptionClient = new SubscriptionClient(apiKey, new HttpClient());


        // STEP 1
        var customers = RetrieveAllCustomersFromCSV(csvPath);
        Console.WriteLine("SUCCESFULLY PARSED CUSTOMERS: " + customers.Count());

        // STEP 2
        //uncomment this if you want to do some cleanup before creating customers (for example deleting customers at some date)
        //var oldCustomers       = await GetAllCustomersAtDate   (customerClient, DateTime.Parse("03/05/2024"));
        //var deletedCustomers   = await DeleteCustomers         (customerClient, oldCustomers);
        //Console.WriteLine("CUSTOMERS TO DELETE: " + oldCustomers.Count);
        //Console.WriteLine("CUSTOMERS DELETED: "   + deletedCustomers.Count);
        var retrievedCustomers = await GetAllCustomers   (customerClient, customers);
        var createdCustomers   = await CreateAllCustomers(customerClient, customers.Where(c => !retrievedCustomers.ContainsKey(c)));
        var allCustomers       = Merge(retrievedCustomers, createdCustomers);
        Console.WriteLine("CUSTOMERS RETRIEVED FROM MOLLIE: " + retrievedCustomers.Count);
        Console.WriteLine("CUSTOMERS CREATED IN MOLLIE: "     + createdCustomers.Count);

        // STEP 3
        var retrievedMandates   = await GetMandateForAllCustomers(mandateClient, allCustomers);
        var unmandatedCustomers = Flatten(allCustomers.Where(c => !retrievedMandates.ContainsKey(c.Key)));
        var createdMandates     = await CreateMandateForAllCustomers(mandateClient, unmandatedCustomers);
        Console.WriteLine("MANDATES RETRIEVED FROM MOLLIE: " + retrievedMandates.Count);
        Console.WriteLine("MANDATES CREATED IN MOLLIE: " + createdMandates.Count);

        // STEP 4
        string? description   = conf["Description"];
        string? webhookUrl    = conf["Webhook"];
        var retrievedSubs     = await GetSubForAllCustomers(subscriptionClient, allCustomers);
        var unsubbedCustomers = Flatten(allCustomers.Where(c => !retrievedSubs.ContainsKey(c.Key)));
        var createdSubs       = await CreateSubscriptionForAllCustomers(subscriptionClient, unsubbedCustomers, description, webhookUrl);
        Console.WriteLine("SUBSCRIPTIONS RETRIEVED FROM MOLLIE: " + retrievedSubs.Count);
        Console.WriteLine("SUBSCRIPTIONS CREATED IN MOLLIE: " + createdSubs.Count);
    }

    #region Step 1 convert csv to c# objects
    static IEnumerable<CustomerCsv> RetrieveAllCustomersFromCSV(string path) { // Parse CSV to get customers
        var config = new CsvConfiguration(CultureInfo.InvariantCulture) {
            Delimiter         = ",",
            BadDataFound      = null,
            MissingFieldFound = null
        };
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, config);
        return csv.GetRecords<CustomerCsv>().ToList();
    }
    #endregion

    #region Step 2 create customers
    static async Task<Dictionary<CustomerCsv, CustomerResponse>> GetAllCustomers(ICustomerClient customerClient, IEnumerable<CustomerCsv> customers) { // Create all customers in API
        Dictionary<CustomerCsv, CustomerResponse> customerResponses = new ();
        try {
            var response = await customerClient.GetCustomerListAsync(limit: 250);
            if(response?.Items != null)
                foreach(var item in response.Items)
                    foreach(var customer in customers)
                    if(
                        (CustomerName(customer) == (item.Name ?? "")) 
                     && ((customer.PrimaireEmail ?? "") == (item.Email ?? "")))
                        customerResponses.Add(
                            customers.Where(c => ((c.Voornaam + " " + c.Achternaam) == item.Name) && ((c.PrimaireEmail ?? "") == (item.Email ?? ""))).First(), 
                            item);
        } catch (Exception e) {
            Console.WriteLine("failed customer retrieval - " + e.Message);
        }
        return customerResponses;
    }
    static async Task<List<CustomerResponse>> GetAllCustomersAtDate(ICustomerClient customerClient, DateTime minimumCreatedAt) {
        List<CustomerResponse> customerResponses = new ();
        try {
            var response = await customerClient.GetCustomerListAsync(limit: 250);
            if(response?.Items != null)
                foreach(var item in response.Items)
                    if(item.CreatedAt >= minimumCreatedAt)
                        customerResponses.Add(item);
        } catch (Exception e) {
            Console.WriteLine("failed customer retrieval - " + e.Message);
        }
        return customerResponses;
    }

    static async Task<List<CustomerResponse>> DeleteCustomers(ICustomerClient customerClient, IEnumerable<CustomerResponse> customers) {
        List<CustomerResponse> customerResponses = new ();
        foreach (var customer in customers) {
            try {
                var task = customerClient.DeleteCustomerAsync(customer.Id);
                await task;
                if(task.IsCompletedSuccessfully)
                    customerResponses.Add(customer);
            } catch (Exception e) {
                Console.WriteLine("failed customer deletion - " + customer.Email + " - " + e.Message);
            }
        }
        return customerResponses;
    }

    static async Task<Dictionary<CustomerCsv, CustomerResponse>> CreateAllCustomers(ICustomerClient customerClient, IEnumerable<CustomerCsv> customers) { // Create all customers in API
        Dictionary<CustomerCsv, CustomerResponse> customerResponses = new ();

        foreach (var customer in customers) {
            try {
                var response = await customerClient.CreateCustomerAsync(new CustomerRequest{
                    Name     = CustomerName(customer),
                    Email    = customer.PrimaireEmail
                });
                if(response != null)
                    customerResponses.Add(customer, response);
            } catch (Exception e) {
                Console.WriteLine("failed customer creation - " + customer.PrimaireEmail + " - " + e.Message);
            }
        }
        return customerResponses;
    }

    private static string CustomerName(CustomerCsv customer) => 
        (customer.Voornaam ?? "") + 
        (!string.IsNullOrEmpty(customer.Voornaam) && !string.IsNullOrEmpty(customer.Achternaam) ? " " : "") + 
        (customer.Achternaam ?? "");
    #endregion

    #region Step 3 create mandates
    static async Task<Dictionary<CustomerCsv, MandateResponse>> GetMandateForAllCustomers(IMandateClient mandateClient, Dictionary<CustomerCsv, CustomerResponse> customers) { //note: date in YYYY-MM-DD
        Dictionary<CustomerCsv, MandateResponse> customerResponses = new ();

        foreach (var customer in customers) {
            try {
                var response = await mandateClient.GetMandateListAsync(customer.Value.Id);
                if(response?.Items?.FirstOrDefault() != null)
                    customerResponses.Add(
                        customer.Key, 
                        response.Items.First());
            } catch (Exception e) {
                Console.WriteLine("failed mandate retrieval - " + customer.Key.PrimaireEmail + " - " + e.Message);
            }
        }
        return customerResponses;
    }
    static async Task<Dictionary<CustomerCsv, MandateResponse>> CreateMandateForAllCustomers(IMandateClient mandateClient, Dictionary<CustomerCsv, CustomerResponse> customers) { //note: date in YYYY-MM-DD
        Dictionary<CustomerCsv, MandateResponse> customerResponses = new ();

        foreach (var customer in customers) {
            try {
                var response = await mandateClient.CreateMandateAsync(customer.Value.Id, new SepaDirectDebitMandateRequest{
                    SignatureDate   = customer.Key.GemachtigdSinds,
                    ConsumerName    = customer.Value.Name,
                    ConsumerAccount = customer.Key.IBAN
                });
                if(response != null)
                    customerResponses.Add(customer.Key, response);
            } catch (Exception e) {
                Console.WriteLine("failed mandate creation - " + customer.Key.PrimaireEmail + " - " + e.Message);
            }
        }
        return customerResponses;
    }
    #endregion

    #region Step 4 create subscriptions
    static async Task<Dictionary<CustomerCsv, SubscriptionResponse>> GetSubForAllCustomers(ISubscriptionClient subscriptionClient, Dictionary<CustomerCsv, CustomerResponse> customers) { //note: date in YYYY-MM-DD
        Dictionary<CustomerCsv, SubscriptionResponse> customerResponses = new ();

        foreach (var customer in customers) {
            try {
                var response = await subscriptionClient.GetSubscriptionListAsync(customer.Value.Id);
                if(response?.Items?.FirstOrDefault() != null)
                    customerResponses.Add(
                        customer.Key, 
                        response.Items.First());
            } catch (Exception e) {
                Console.WriteLine("failed subscription retrieval - " + customer.Key.PrimaireEmail + " - " + e.Message);
            }
        }
        return customerResponses;
    }
    static async Task<Dictionary<CustomerCsv, SubscriptionResponse>> CreateSubscriptionForAllCustomers(
        ISubscriptionClient                       subscriptionClient, 
        Dictionary<CustomerCsv, CustomerResponse> customers,
        string? description = "",
        string? webhookUrl  = ""
    ) {
        Dictionary<CustomerCsv, SubscriptionResponse> customerResponses = new ();

        foreach (var customer in customers) {
            try {
                var response = await subscriptionClient.CreateSubscriptionAsync(customer.Value.Id, new SubscriptionRequest{
                    Amount      = new Amount{Currency = Currency.EUR, Value = customer.Key.DonatieBedrag.ToString("0.00")},
                    Interval    = "1 month",
                    Description = description,
                    WebhookUrl  = webhookUrl
                });
                if(response != null)
                    customerResponses.Add(customer.Key, response);
            } catch (Exception e) {
                Console.WriteLine("failed subscription creation - " + customer.Key.PrimaireEmail + " - " + e.Message);
            }
        }
        return customerResponses;
    }
    #endregion

    private static Dictionary<T1, T2> Merge<T1, T2>(Dictionary<T1, T2> d1, Dictionary<T1, T2> d2) where T1 : notnull {
        Dictionary<T1, T2> d = new();
        foreach (var item in d1)
            d.Add(item.Key, item.Value);
        foreach (var item in d2)
            d.Add(item.Key, item.Value);
        return d;
    }
    private static Dictionary<T1, T2> Flatten<T1, T2>(IEnumerable<KeyValuePair<T1, T2>> d1) where T1 : notnull {
        Dictionary<T1, T2> d = new();
        foreach (var item in d1)
            d.Add(item.Key, item.Value);
        return d;
    }

}

public class CustomerCsv {
    [Name("Voornaam")]
    public required string Voornaam {get; set;}
    [Name("Tussenvoegsel")]
    public string? Tussenvoegsel {get; set;}
    [Name("Achternaam")]
    public required string Achternaam {get; set;}
    [Name("Primaire E-Mail")]
    public string? PrimaireEmail {get; set;}
    [Name("IBAN")]
    public required string IBAN {get; set;}
    [Name("Donatie bedrag")]
    public float DonatieBedrag {get; set;}
    [Name("Gemachtigd sinds")]
    public DateTime? GemachtigdSinds {get; set;}
}