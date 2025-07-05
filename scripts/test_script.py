import ssl
import datetime
import os
from tcms_api import TCMS


def create_connection():
    #AUTH into Kiwi
    try:
        _create_unverified_https_context = ssl._create_unverified_context
    except AttributeError:
        # Legacy Python that doesn't verify HTTPS certificates by default
        pass
    else:
        # Handle target environment that doesn't support HTTPS verification
        ssl._create_default_https_context = _create_unverified_https_context

    rpc_client = TCMS()
    return rpc_client


def search_testplan(rpc_client, testname: str) -> int:
    for testplan in rpc_client.exec.TestPlan.filter({'name': testname}):
        return testplan['id']


def add_testcases(rpc_client, testrun):
    rpc_client.exec.TestRun.add_case(testrun['id'], 7508)
    rpc_client.exec.TestRun.add_case(testrun['id'], 7509)


def update_testexecution(rpc_client, testrun):
    for testexecution in rpc_client.exec.TestRun.get_cases(testrun['id']):
        if testName == 'CorrectTest':
            rpc_client.exec.TestExecution.update(testexecution['execution_id'], {'status': '4'})
        else:
            rpc_client.exec.TestExecution.update(testexecution['execution_id'], {'status': '5'})


def get_latest_file(directory):
    entries = os.scandir(directory)
    latest_file = None
    latest_time = 0
    
    for entry in entries:
        if entry.is_file():
            creation_time = entry.stat().st_birthtime
            if creation_time > latest_time:
                latest_time = creation_time
                latest_file = entry.path
    
    return latest_file

directory = "C:\\Users\\User\\Downloads\\Telegram Desktop"
rpc_client = create_connection()


testplan_id = search_testplan(rpc_client, testName)
notes = f'''
    Test Flow: {timeFlow}
    Brunch Name: {brunchName}
    Commit: {commit}
    Device: The test was executed on an {deviceInfo} device
    Other Errors: {otherErrors} (More info in report)
'''


testrun_params = {
    'build': '101',
    'product__name': 'Citrus',
    'manager': 'vslobodchikov',
    'default_tester': 'vslobodchikov',
    'plan': testplan_id,
    'summary': f'{testName}',
    'notes': notes,
    'start_date': datetime.datetime.now()
}

testrun = rpc_client.exec.TestRun.create(testrun_params)

reportTXT = get_latest_file(directory)
add_testcases(rpc_client, testrun)
update_testexecution(rpc_client, testrun)
rpc_client.exec.TestRun.add_attachment(testrun['id'], reportTXT, 'UTF-8')

