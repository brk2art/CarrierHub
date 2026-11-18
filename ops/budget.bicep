targetScope = 'subscription'

param amount int = 10
param startDate string = '2026-11-01'
param endDate string = '2027-12-31'
param contactEmails array

resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
    name: 'carrierhub-monthly'
    properties: {
        category: 'Cost'
        amount: amount
        timeGrain: 'Monthly'
        timePeriod: {
            startDate: startDate
            endDate: endDate
        }
        notifications: {
            actual50: {
                enabled: true
                operator: 'GreaterThan'
                threshold: 50
                thresholdType: 'Actual'
                contactEmails: contactEmails
            }
            actual80: {
                enabled: true
                operator: 'GreaterThan'
                threshold: 80
                thresholdType: 'Actual'
                contactEmails: contactEmails
            }
            forecast100: {
                enabled: true
                operator: 'GreaterThan'
                threshold: 100
                thresholdType: 'Forecasted'
                contactEmails: contactEmails
            }
        }
    }
}
